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
using System.Diagnostics.CodeAnalysis;

namespace WhereTheyGo
{
    // Reads the existing transit system: which lines exist, which stops they serve in
    // order, and how loaded they are. The judging is LineWindow (pure).
    //
    // Ordering matters and is easy to get wrong: only RouteWaypoint/RouteSegment on
    // the LINE are in travel order. The ConnectedRoute buffer on a stop is built by
    // iterating waypoints in arbitrary chunk order, so it must never be used to infer
    // a line's sequence.
    internal static class Lines
    {
        // Every passenger line of a modelled mode with at least two waypoints, in query
        // order — the one loop both the full collection and the reading share.
        private static void ForEachLine(EntityManager entityManager, EntityQuery lineQuery, Action<Entity, TransportLine, TransportLineData, DynamicBuffer<RouteWaypoint>> visit)
        {
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

                // Air, ship, taxi and the rest are passenger transport too, but this mod
                // plans surface transit: it cannot re-route an air line and has no
                // alignment for one. They were being folded in as buses, which put
                // 23 km "bus lines" in the health list and skewed the city median.
                if (!IsModelled(lineData.m_TransportType))
                {
                    continue;
                }

                if (!entityManager.TryGetBuffer(lineEntity, isReadOnly: true, out DynamicBuffer<RouteWaypoint> waypoints) ||
                    waypoints.Length < 2)
                {
                    continue;
                }

                visit(lineEntity, transportLine, lineData, waypoints);
            }
        }

        public static void Collect(
            EntityManager entityManager,
            EntityQuery lineQuery,
            PrefabSystem prefabSystem,
            Game.UI.NameSystem nameSystem,
            List<ExistingLine> lines,
            List<float2Like> stopPositions,
            Dictionary<Entity, int> stopIndices,
            Dictionary<int, Entity> lineEntities)
        {
            lines.Clear();
            stopPositions.Clear();
            stopIndices.Clear();
            lineEntities.Clear();
            var ignored = new List<string>();

            ForEachLine(entityManager, lineQuery, (lineEntity, transportLine, lineData, waypoints) =>
            {
                var line = new ExistingLine
                {
                    m_Id = IdentityOf(lineEntity),
                    m_EntityIndex = lineEntity.Index,
                    m_Mode = ModeOf(lineData.m_TransportType),
                    m_Name = ResolveName(entityManager, nameSystem, prefabSystem, lineEntity, lineData.m_TransportType),
                    m_Schedule = ScheduleOf(entityManager, lineEntity),
                };

                // A line with no segments is still readable; the reader handles an empty buffer.
                _ = entityManager.TryGetBuffer(lineEntity, isReadOnly: true, out DynamicBuffer<RouteSegment> segments);
                ReadWaypoints(entityManager, waypoints, segments, line, stopPositions, stopIndices);

                if (line.m_StopIndices.Count < 2)
                {
                    return;
                }

                // Outside connections are outside the mod's model (user decision
                // 2026-09-04, reaffirmed 2026-09-06), so a line that cannot carry a
                // single journey BETWEEN TWO PLACES IN THE CITY carries only travellers
                // the demand model never sees: it is neither judged nor routed over.
                // Counted rather than "calls at one at all", so a regional train that
                // also serves three city stations keeps them. Valmare's three train
                // lines had no city stop at all.
                int cityStops = CityStopCount(entityManager, waypoints);
                if (cityStops < 2)
                {
                    ignored.Add($"{line.m_Name} ({cityStops.ToString(CultureInfo.InvariantCulture)} of {line.m_StopIndices.Count.ToString(CultureInfo.InvariantCulture)} stops in the city)");
                    return;
                }

                ReadVehicles(entityManager, lineEntity, out line.m_Vehicles, out line.m_Passengers, out line.m_Capacity);

                float dwell = lineData.m_StopDuration;
                line.m_VehicleInterval = transportLine.m_VehicleInterval;

                // Mirrors TransportLineSystem.RefreshLineSegments' stableDuration and
                // the target interval it feeds to CalculateVehicleCount.
                line.m_StableDurationSeconds = line.m_LineDurationSeconds + (line.m_StopIndices.Count * dwell);
                line.m_TargetInterval = TargetInterval(entityManager, lineEntity, lineData);
                line.m_StopDuration = dwell;

                TransportLineFlags flags = transportLine.m_Flags;
                line.m_RequireVehicles = (flags & TransportLineFlags.RequireVehicles) != 0;
                line.m_NotEnoughVehicles = (flags & TransportLineFlags.NotEnoughVehicles) != 0;

                lines.Add(line);
                lineEntities[line.m_Id] = lineEntity;
            });

            if (ignored.Count > 0)
            {
                DeferredLog.Info(
                    $"Lines ignored — fewer than two stops inside the city, so they carry only outside connections the demand model does not see: {string.Join("; ", ignored)}");
            }
        }

        // How many of the line's stops are in the city rather than on an outside
        // connection. A stop belongs to an outside connection when
        // Game.Objects.OutsideConnection sits on it or anywhere up its Owner chain
        // (the stop is owned by the station building, which the game marks).
        private static int CityStopCount(EntityManager entityManager, DynamicBuffer<RouteWaypoint> waypoints)
        {
            int cityStops = 0;
            for (int w = 0; w < waypoints.Length; w++)
            {
                if (!entityManager.TryGetComponent(waypoints[w].m_Waypoint, out Connected connected)
                    || !entityManager.HasComponent<Game.Routes.TransportStop>(connected.m_Connected))
                {
                    continue;
                }

                if (!IsOutsideConnection(entityManager, connected.m_Connected))
                {
                    cityStops++;
                }
            }

            return cityStops;
        }

        private static bool IsOutsideConnection(EntityManager entityManager, Entity stop)
        {
            Entity owner = stop;
            for (int depth = 0; depth < 4 && owner != Entity.Null; depth++)
            {
                if (entityManager.HasComponent<Game.Objects.OutsideConnection>(owner))
                {
                    return true;
                }

                owner = entityManager.TryGetComponent(owner, out Game.Common.Owner next) ? next.m_Owner : Entity.Null;
            }

            return false;
        }

        // One reading of every line — the counts the rolling window keeps (register
        // A8.5) — without rebuilding the collection the route worker may be reading.
        public static void Observe(EntityManager entityManager, EntityQuery lineQuery, uint frame, float timeOfDay, LineHistory history, HashSet<int> liveLineIds)
        {
            liveLineIds.Clear();
            ForEachLine(entityManager, lineQuery, (lineEntity, transportLine, lineData, waypoints) =>
            {
                if (CityStopCount(entityManager, waypoints) < 2)
                {
                    return;
                }

                int id = IdentityOf(lineEntity);
                _ = liveLineIds.Add(id);
                ReadVehicles(entityManager, lineEntity, out int vehicles, out int passengers, out int capacity);
                history.Record(id, new LineObservation
                {
                    m_Frame = frame,
                    m_Passengers = passengers,
                    m_Capacity = capacity,
                    m_IntervalSeconds = transportLine.m_VehicleInterval,
                    m_Vehicles = vehicles,
                    m_TimeOfDay = timeOfDay,
                });
            });
            history.RetainOnly(liveLineIds);
        }

        // The interval the player set: the prefab default with the line's own
        // vehicle-count modifier applied (RouteUtils.ApplyModifier).
        private static float TargetInterval(EntityManager entityManager, Entity lineEntity, TransportLineData lineData)
        {
            float targetInterval = lineData.m_DefaultVehicleInterval;
            if (entityManager.TryGetBuffer(lineEntity, isReadOnly: true, out DynamicBuffer<RouteModifier> modifiers))
            {
                RouteUtils.ApplyModifier(ref targetInterval, modifiers, RouteModifierType.VehicleInterval);
            }

            return targetInterval;
        }

        // A line identity that survives its neighbours being deleted.
        //
        // Entity.Index alone does NOT: the ECS hands a freed index to the next entity
        // created, so deleting a line and laying another could put the new one on the
        // old one's index. LineHistory.RetainOnly then saw a live id and kept the
        // series, and the new line's verdict, mean usage, peak and "N readings over
        // M h" were all drawn from a line that no longer existed — with nothing in the
        // log to say so. Version is exactly what distinguishes the two.
        //
        // Packed into one int because the panel round-trips this value through a
        // binding and back through the improveLine trigger. 24 bits is far more index
        // than a city's entity count reaches.
        private static int IdentityOf(Entity line)
        {
            return (line.Index & 0xFFFFFF) | ((line.Version & 0xFF) << 24);
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
            List<float2Like> stopPositions,
            Dictionary<Entity, int> stopIndices)
        {
            float pendingSeconds = 0f;

            for (int w = 0; w < waypoints.Length; w++)
            {
                Entity waypoint = waypoints[w].m_Waypoint;

                if (entityManager.TryGetComponent(waypoint, out Connected connected) &&
                    entityManager.HasComponent<Game.Routes.TransportStop>(connected.m_Connected))
                {
                    Entity stop = connected.m_Connected;
                    if (!stopIndices.TryGetValue(stop, out int index))
                    {
                        index = stopPositions.Count;
                        stopIndices[stop] = index;
                        stopPositions.Add(entityManager.TryGetComponent(waypoint, out Game.Routes.Position stopPosition)
                            ? new float2Like(stopPosition.m_Position.x, stopPosition.m_Position.z)
                            : float2Like.Zero);
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
        }

        // Fleet size, and how full it is. Capacity comes from the vehicle prefab, and
        // multi-unit consists are walked through their layout so a coupled train
        // counts every carriage.
        private static void ReadVehicles(EntityManager entityManager, Entity lineEntity, out int vehicles, out int passengers, out int capacity)
        {
            vehicles = 0;
            passengers = 0;
            capacity = 0;
            if (!entityManager.TryGetBuffer(lineEntity, isReadOnly: true, out DynamicBuffer<RouteVehicle> fleet))
            {
                return;
            }

            vehicles = fleet.Length;
            for (int v = 0; v < fleet.Length; v++)
            {
                Entity vehicle = fleet[v].m_Vehicle;
                if (entityManager.TryGetBuffer(vehicle, isReadOnly: true, out DynamicBuffer<LayoutElement> layout) && layout.Length > 0)
                {
                    for (int u = 0; u < layout.Length; u++)
                    {
                        AddUnit(entityManager, layout[u].m_Vehicle, ref passengers, ref capacity);
                    }
                }
                else
                {
                    AddUnit(entityManager, vehicle, ref passengers, ref capacity);
                }
            }
        }

        private static void AddUnit(EntityManager entityManager, Entity unit, ref int passengers, ref int capacity)
        {
            if (!entityManager.TryGetComponent(unit, out PrefabRef prefabRef) ||
                !entityManager.TryGetComponent(prefabRef.m_Prefab, out PublicTransportVehicleData vehicleData))
            {
                return;
            }

            capacity += vehicleData.m_PassengerCapacity;
            if (entityManager.TryGetBuffer(unit, isReadOnly: true, out DynamicBuffer<Passenger> passengerBuffer))
            {
                passengers += passengerBuffer.Length;
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
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "A line's display name is cosmetic. Any surprise from the game's " +
                "naming or localization APIs is logged and the mode name used instead, rather " +
                "than losing the whole line-health pass.")]
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
                if (nameSystem is not null
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
                    if (dictionary is not null && dictionary.TryGetValue(id, out string pattern)
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

        // The modes this mod can measure, advise on and draw an alignment for.
        public static bool IsModelled(TransportType type)
        {
            switch (type)
            {
                case TransportType.Bus:
                case TransportType.Tram:
                case TransportType.Subway:
                case TransportType.Train:
                case TransportType.Ferry:
                    return true;
                default:
                    return false;
            }
        }

        public static ModePreset ModeOf(TransportType type)
        {
            switch (type)
            {
                case TransportType.Bus: return ModePreset.Bus;
                case TransportType.Tram: return ModePreset.Tram;
                case TransportType.Subway: return ModePreset.Metro;
                case TransportType.Train: return ModePreset.Train;
                case TransportType.Ferry: return ModePreset.Ferry;
                default: return ModePreset.Bus;
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
                    m_ExpectedWait = line.ExpectedWait,
                    // Per mode rather than a flat 10 m/s for everything: this is the
                    // fallback when a route segment carries no pathfound duration, and
                    // a train covering ground at a bus's speed made its rides look
                    // three times longer than they are.
                    m_SpeedMetresPerSecond = Assumptions.CruiseSpeedFor(line.m_Mode),
                });
            }

            return result;
        }

        // The line's schedule as the player set it (ScheduleSection: the Day policy
        // marks a day-only line, the Night policy a night-only one, neither = all day).
        private static LineSchedule ScheduleOf(EntityManager entityManager, Entity lineEntity)
        {
            if (!entityManager.TryGetComponent(lineEntity, out Route route))
            {
                return LineSchedule.DayAndNight;
            }

            if (RouteUtils.CheckOption(route, RouteOption.Day))
            {
                return LineSchedule.Day;
            }

            return RouteUtils.CheckOption(route, RouteOption.Night) ? LineSchedule.Night : LineSchedule.DayAndNight;
        }
    }
}
