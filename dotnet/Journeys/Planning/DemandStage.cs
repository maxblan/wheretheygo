using System.Collections.Generic;
using System.Threading;

namespace WhereTheyGo
{
    // The demand refresh's pure half, run on the worker ahead of the routing: the
    // journeys put in their total order, the served-walk field rebuilt when the stops
    // or the network moved, both ends of every journey snapped, the coverage share
    // measured on them, and the door pairs the routing then searches.
    //
    // It used to run on the main thread, and every piece of it grows with the city: on
    // a synthetic city of a hundred thousand the sort, the snap of every journey end
    // and the pair table came to over a hundred milliseconds even under .NET 9, and the
    // game's Mono is about three times slower. That was a visible freeze every thirty
    // seconds.
    //
    // Ownership: the main thread fills the inputs, hands the stage over, and does not
    // touch it, nor the buffers in it, again until Done reads true. Everything Run
    // writes is published by that one flag.
    internal sealed class DemandStage
    {
        private bool m_Done;

        // ---- In
        // The trips as the extraction and the observation drained them, unordered.
        // Emptied by Run once ordered, so the buffer can be refilled next refresh.
        public List<Journey> Trips = new List<Journey>();

        // Where the ordered journeys go. A buffer kept between refreshes; Run clears it.
        public List<Journey> Journeys = new List<Journey>();

        // The tile snap the coverage is measured on, and its memo. Without a snap
        // there is no pedestrian network yet, and nothing is snapped or measured.
        public TileSnap? Snap;
        public SnapMemo? Memo;

        // The served stops and the player's horizon; HorizonMs is what the share,
        // the field and the walked-journey test in the routing all read.
        public float2Like[] Stops = System.Array.Empty<float2Like>();
        public int HorizonMs;

        // Whether the served-walk field must be rebuilt, the Signature it will then
        // carry, and the grid it is laid out on with the world corner that grid
        // starts at. When not rebuilt, Served is the field
        // in place and is only read.
        public bool RebuildField;
        public long FieldSignature;
        public int2Like FieldGrid;
        public float2Like FieldWorldMin;
        public int[]? Served;

        // Buffers kept between refreshes; Run may replace either if it does not fit.
        public IntDijkstra? Dijkstra;
        public JourneyEnds? Ends;

        // ---- Out
        public int TripCount;
        public float TotalWeight;
        public RoutingProblem Pairs = new RoutingProblem();
        public CoverageReport? Report;
        public ServedWalkField? Field;
        public bool FieldRebuilt;
        public long SortMs;
        public long FieldMs;
        public long SnapMs;
        public long MeasureMs;
        public long PairMs;

        // Set by the main thread once it has adopted the stage.
        public bool Adopted;

        public bool Done => Volatile.Read(ref m_Done);

        public void Run(float2Like worldMin, int2Like zoneGrid)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TotalWeight = DemandZones.Aggregate(Trips, worldMin, zoneGrid, Journeys, out TripCount);
            Trips.Clear();
            SortMs = clock.ElapsedMilliseconds;

            WalkGraph? graph = Snap?.Graph;
            WalkNodeIndex? index = Snap?.Index;
            if (Snap is not null && graph is not null && index is not null && Memo is not null)
            {
                MeasureCoverage(Snap, graph, index, Memo, clock);
            }

            clock.Restart();
            Pairs = DoorPairs.Build(Journeys);
            PairMs = clock.ElapsedMilliseconds;
            Volatile.Write(ref m_Done, true);
        }

        private void MeasureCoverage(TileSnap snap, WalkGraph graph, WalkNodeIndex index, SnapMemo memo, System.Diagnostics.Stopwatch clock)
        {
            clock.Restart();
            int[]? served = Served;
            if (RebuildField || served is null)
            {
                if (Dijkstra is null || Dijkstra.Dist.Length != graph.NodeCount)
                {
                    Dijkstra = new IntDijkstra(graph.NodeCount);
                }

                ServedWalkField.SnapStops(Stops, index, Assumptions.AccessWalkMs, out int[] stopNodes, out int[] stopAccess);
                // Searched well past the coverage horizon on purpose. Every "is this
                // served" test compares against the horizon itself (Coverage.EndServed),
                // so the coverage figure is unchanged; what the extra range buys is a
                // real number for the buildings beyond it. A house 12 minutes from the
                // nearest stop and a house 40 minutes away are different problems, and
                // "over 10 min" for both reads as a broken measurement rather than a
                // long walk.
                served = Coverage.ServedWalkMs(
                    graph, Dijkstra, stopNodes, stopAccess, stopNodes.Length,
                    HorizonMs * Assumptions.AccessFieldHorizonMultiple);
                Served = served;
                Field = ServedWalkField.Build(snap, served, HorizonMs, FieldGrid);
                FieldRebuilt = true;
            }

            FieldMs = clock.ElapsedMilliseconds;

            // The SHARE does follow the journeys, so it is measured every refresh: one
            // lookup per journey end into the field above.
            clock.Restart();
            memo.ResetSearches();
            JourneyEnds ends = JourneyEnds.Snap(Journeys, memo, Ends);
            Ends = ends;
            SnapMs = clock.ElapsedMilliseconds;
            clock.Restart();
            Report = Coverage.Measure(
                served, HorizonMs,
                ends.OriginNode, ends.OriginAccessMs, ends.DestinationNode, ends.DestinationAccessMs,
                ends.Weight, ends.Count);
            MeasureMs = clock.ElapsedMilliseconds;
        }
    }
}
