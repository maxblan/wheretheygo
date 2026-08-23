using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Entities;
using Game.Pathfind;
using Game.SceneFlow;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace StationSuitabilityOverlay
{
    // One existing transit line, read out of the save: the stops it calls at in
    // travel order, how long each hop takes, how full it is, and the geometry needed
    // to draw it.
    internal sealed class ExistingLine
    {
        public Entity m_Entity;
        public Setting.ModePreset m_Mode;
        // The name the game shows for this line, so the panel and the Transportation
        // Overview agree instead of the mod inventing its own numbering.
        public string m_Name;
        public readonly List<int> m_StopIndices = new List<int>();
        public readonly List<float> m_RideSeconds = new List<float>();
        public readonly List<float2> m_Path = new List<float2>();
        public float m_ExpectedWait;
        // The line's own headway, as the game maintains it.
        public float m_VehicleInterval;
        public float m_LineDurationSeconds;
        public float m_LengthMetres;
        public int m_Vehicles;
        public int m_Passengers;
        public int m_Capacity;
        public float m_AverageWait;
        public bool m_RequireVehicles;
        public bool m_NotEnoughVehicles;
    }

    // Reads the existing transit system: which lines exist, which stops they serve in
    // order, and how loaded they are.
    //
    // Ordering matters and is easy to get wrong: only RouteWaypoint/RouteSegment on
    // the LINE are in travel order. The ConnectedRoute buffer on a stop is built by
    // iterating waypoints in arbitrary chunk order, so it must never be used to infer
    // a line's sequence.
    internal static class SuitabilityLines
    {
        public static void Collect(
            EntityManager entityManager,
            EntityQuery lineQuery,
            PrefabSystem prefabSystem,
            Game.UI.NameSystem nameSystem,
            List<ExistingLine> lines,
            List<float2> stopPositions,
            Dictionary<Entity, int> stopIndices)
        {
            lines.Clear();
            stopPositions.Clear();
            stopIndices.Clear();

            using var entities = lineQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                Entity lineEntity = entities[i];
                if (!entityManager.TryGetComponent(lineEntity, out TransportLine transportLine) ||
                    !entityManager.TryGetComponent(lineEntity, out PrefabRef prefabRef))
                {
                    continue;
                }

                if (!entityManager.TryGetComponent(prefabRef.m_Prefab, out TransportLineData lineData) ||
                    !lineData.m_PassengerTransport)
                {
                    continue;
                }

                if (!entityManager.TryGetBuffer(lineEntity, true, out DynamicBuffer<RouteWaypoint> waypoints) ||
                    waypoints.Length < 2)
                {
                    continue;
                }

                var line = new ExistingLine
                {
                    m_Entity = lineEntity,
                    m_Mode = ModeOf(lineData.m_TransportType),
                    m_Name = ResolveName(entityManager, nameSystem, prefabSystem, lineEntity, lineData.m_TransportType),
                };

                entityManager.TryGetBuffer(lineEntity, true, out DynamicBuffer<RouteSegment> segments);
                ReadWaypoints(entityManager, waypoints, segments, line, stopPositions, stopIndices);

                if (line.m_StopIndices.Count < 2)
                {
                    continue;
                }

                ReadVehicles(entityManager, lineEntity, line);

                // The wait a rider actually experiences, the way the game's own
                // pathfinder computes it.
                float dwell = lineData.m_StopDuration;
                line.m_VehicleInterval = transportLine.m_VehicleInterval;
                line.m_ExpectedWait = SuitabilityTransit.ExpectedWait(
                    transportLine.m_VehicleInterval, line.m_AverageWait, dwell);

                TransportLineFlags flags = transportLine.m_Flags;
                line.m_RequireVehicles = (flags & TransportLineFlags.RequireVehicles) != 0;
                line.m_NotEnoughVehicles = (flags & TransportLineFlags.NotEnoughVehicles) != 0;

                lines.Add(line);
            }
        }

        // Walks the line's waypoints in travel order. Not every waypoint is a stop —
        // shaping waypoints have no TransportStop behind them — but the segment
        // buffer is index-aligned with the waypoints, so hop durations accumulate
        // across skipped waypoints rather than being lost.
        private static void ReadWaypoints(
            EntityManager entityManager,
            DynamicBuffer<RouteWaypoint> waypoints,
            DynamicBuffer<RouteSegment> segments,
            ExistingLine line,
            List<float2> stopPositions,
            Dictionary<Entity, int> stopIndices)
        {
            float pendingSeconds = 0f;
            float waitSum = 0f;
            int waitCount = 0;

            for (int w = 0; w < waypoints.Length; w++)
            {
                Entity waypoint = waypoints[w].m_Waypoint;

                if (entityManager.TryGetComponent(waypoint, out Game.Routes.Position position))
                {
                    line.m_Path.Add(new float2(position.m_Position.x, position.m_Position.z));
                }

                if (entityManager.TryGetComponent(waypoint, out WaitingPassengers waiting))
                {
                    waitSum += waiting.m_AverageWaitingTime;
                    waitCount++;
                }

                if (entityManager.TryGetComponent(waypoint, out Connected connected) &&
                    entityManager.HasComponent<Game.Routes.TransportStop>(connected.m_Connected))
                {
                    Entity stop = connected.m_Connected;
                    if (!stopIndices.TryGetValue(stop, out int index))
                    {
                        index = stopPositions.Count;
                        stopIndices[stop] = index;
                        stopPositions.Add(entityManager.TryGetComponent(waypoint, out Game.Routes.Position stopPosition)
                            ? new float2(stopPosition.m_Position.x, stopPosition.m_Position.z)
                            : float2.zero);
                    }

                    line.m_StopIndices.Add(index);
                    line.m_RideSeconds.Add(pendingSeconds);
                    pendingSeconds = 0f;
                }

                // Segment w is the hop from waypoint w to waypoint w+1.
                if (w < segments.Length && entityManager.TryGetComponent(segments[w].m_Segment, out PathInformation path))
                {
                    pendingSeconds += path.m_Duration;
                    line.m_LengthMetres += path.m_Distance;
                    line.m_LineDurationSeconds += path.m_Duration;
                }
            }

            line.m_AverageWait = waitCount > 0 ? waitSum / waitCount : 0f;
        }

        // Fleet size, and how full it is. Capacity comes from the vehicle prefab, and
        // multi-unit consists are walked through their layout so a coupled train
        // counts every carriage.
        private static void ReadVehicles(EntityManager entityManager, Entity lineEntity, ExistingLine line)
        {
            if (!entityManager.TryGetBuffer(lineEntity, true, out DynamicBuffer<RouteVehicle> vehicles))
            {
                return;
            }

            line.m_Vehicles = vehicles.Length;
            for (int v = 0; v < vehicles.Length; v++)
            {
                Entity vehicle = vehicles[v].m_Vehicle;
                if (entityManager.TryGetBuffer(vehicle, true, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0)
                {
                    for (int u = 0; u < layout.Length; u++)
                    {
                        AddUnit(entityManager, layout[u].m_Vehicle, line);
                    }
                }
                else
                {
                    AddUnit(entityManager, vehicle, line);
                }
            }
        }

        private static void AddUnit(EntityManager entityManager, Entity unit, ExistingLine line)
        {
            if (!entityManager.TryGetComponent(unit, out PrefabRef prefabRef) ||
                !entityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData vehicleData))
            {
                return;
            }

            line.m_Capacity += vehicleData.m_PassengerCapacity;
            if (entityManager.TryGetBuffer(unit, true, out DynamicBuffer<Passenger> passengers))
            {
                line.m_Passengers += passengers.Length;
            }
        }

        // The name the game shows for a line, reproduced rather than approximated.
        //
        // NameSystem.GetRenderedLabelName is NOT usable here: for an unnamed line the
        // game returns Name.FormattedName("Assets.ROUTE_NAME[Bus Line]", "NUMBER", n)
        // and the UI layer substitutes the argument, so the C# label helper hands back
        // the raw pattern ("Buslinie {NUMBER}") with no number in it — every bus line
        // would read the same. So this follows NameSystem.GetRouteName and does the
        // substitution the UI would have done.
        private static string ResolveName(
            EntityManager entityManager,
            Game.UI.NameSystem nameSystem,
            PrefabSystem prefabSystem,
            Entity lineEntity,
            TransportType type)
        {
            try
            {
                // A line the player renamed: that name is what the Transportation
                // Overview shows, so it wins.
                if (nameSystem != null
                    && nameSystem.TryGetCustomName(lineEntity, out string custom)
                    && !string.IsNullOrEmpty(custom))
                {
                    return Sanitize(custom);
                }

                string number = entityManager.TryGetComponent(lineEntity, out RouteNumber routeNumber)
                    ? routeNumber.m_Number.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

                if (entityManager.TryGetComponent(lineEntity, out PrefabRef prefabRef)
                    && prefabSystem.TryGetPrefab(prefabRef, out RoutePrefab routePrefab))
                {
                    string id = routePrefab.m_LocaleID + "[" + routePrefab.name + "]";
                    var dictionary = GameManager.instance?.localizationManager?.activeDictionary;
                    if (dictionary != null && dictionary.TryGetValue(id, out string pattern)
                        && !string.IsNullOrEmpty(pattern))
                    {
                        return Sanitize(pattern.Replace("{NUMBER}", number));
                    }

                    // No locale entry: the prefab name plus the number still identifies
                    // the line, which is the point.
                    return Sanitize((routePrefab.name + " " + number).Trim());
                }

                if (number.Length > 0)
                {
                    return $"{ModeOf(type)} {number}";
                }
            }
            catch (Exception e)
            {
                // Naming is cosmetic; never let it break the health pass.
                Mod.Log.Warn($"Could not resolve the game's name for a transport line: {e.Message}");
            }

            return ModeOf(type).ToString();
        }

        // The health rows reach the panel as a '|'-delimited, newline-separated string,
        // and a renamed line may contain either character.
        private static string Sanitize(string name)
        {
            return name.Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        public static Setting.ModePreset ModeOf(TransportType type)
        {
            switch (type)
            {
                case TransportType.Tram: return Setting.ModePreset.Tram;
                case TransportType.Subway: return Setting.ModePreset.Metro;
                case TransportType.Train: return Setting.ModePreset.Train;
                case TransportType.Ferry: return Setting.ModePreset.Ferry;
                default: return Setting.ModePreset.Bus;
            }
        }

        // Turns the gathered lines into the router's view of them.
        public static List<TransitLine> ToTransitLines(List<ExistingLine> lines)
        {
            var result = new List<TransitLine>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                ExistingLine line = lines[i];
                result.Add(new TransitLine
                {
                    m_Stops = line.m_StopIndices.ToArray(),
                    m_RideSeconds = line.m_RideSeconds.ToArray(),
                    m_ExpectedWait = line.m_ExpectedWait,
                    m_SpeedMetresPerSecond = 10f,
                });
            }

            return result;
        }

        // Health verdict per line, worst first.
        public static void Judge(List<ExistingLine> lines, List<LineHealth> health)
        {
            health.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                ExistingLine line = lines[i];
                float usage = line.m_Capacity > 0 ? (float)line.m_Passengers / line.m_Capacity : 0f;

                // The fleet the line's own interval implies. The game converges
                // m_VehicleInterval to lineDuration / fleetSize, so inverting it with
                // the RAW interval gives the target it is aiming at. Using the derived
                // expected wait here produced targets of 1 against fleets of 6.
                int target = line.m_VehicleInterval > 0f && line.m_LineDurationSeconds > 0f
                    ? math.max(1, (int)math.round(line.m_LineDurationSeconds / math.max(1f, line.m_VehicleInterval)))
                    : math.max(1, line.m_Vehicles);

                LineVerdict verdict = SuitabilityLineHealth.Judge(
                    usage, line.m_AverageWait, line.m_Vehicles, target,
                    line.m_RequireVehicles, line.m_NotEnoughVehicles, out int addVehicles);

                health.Add(new LineHealth
                {
                    m_Index = i + 1,
                    m_Name = line.m_Name,
                    m_Mode = line.m_Mode,
                    m_Vehicles = line.m_Vehicles,
                    m_TargetVehicles = target,
                    m_Passengers = line.m_Passengers,
                    m_Capacity = line.m_Capacity,
                    m_Usage = usage,
                    m_AverageWait = line.m_AverageWait,
                    m_LengthKm = line.m_LengthMetres / 1000f,
                    m_Stops = line.m_StopIndices.Count,
                    m_Verdict = verdict,
                    m_AddVehicles = addVehicles,
                });
            }

            for (int i = 0; i < lines.Count; i++)
            {
                ExistingLine line = lines[i];
                Mod.Log.Info(
                    $"Line {i + 1} \"{line.m_Name}\" inputs: mode={line.m_Mode}, stops={line.m_StopIndices.Count}, " +
                    $"len={line.m_LengthMetres:F0}m, duration={line.m_LineDurationSeconds:F0}s, " +
                    $"interval={line.m_VehicleInterval:F0}s, expectedWait={line.m_ExpectedWait:F0}s, " +
                    $"observedWait={line.m_AverageWait:F0}, vehicles={line.m_Vehicles}, " +
                    $"aboard={line.m_Passengers}/{line.m_Capacity}, " +
                    $"flags(require={line.m_RequireVehicles}, notEnough={line.m_NotEnoughVehicles})");
            }

            health.Sort((a, b) =>
            {
                int bySeverity = b.Severity.CompareTo(a.Severity);
                return bySeverity != 0 ? bySeverity : b.m_Usage.CompareTo(a.m_Usage);
            });
        }
    }
}
