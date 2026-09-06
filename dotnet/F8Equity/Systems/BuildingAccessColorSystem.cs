using Game;
using Game.Common;
using Game.Objects;
using Game.Rendering;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Color = Game.Objects.Color;
using Transform = Game.Objects.Transform;

namespace TransitArchitect
{
    // Colours every building by how far its door is from a served stop, while the
    // transit-access infomode is on (author's request 2026-09-06).
    //
    // Why a system of its own, between two of the game's. A building's colour lives in
    // Game.Objects.Color {m_Index, m_Value}: the index selects one of the infomode
    // colour slots the shader holds, the value walks its gradient. Game.Rendering.
    // ObjectColorSystem writes that component every frame an infoview is open, and
    // Game.Rendering.BatchDataSystem reads it — both in SystemUpdatePhase.Rendering,
    // in that order. The vanilla producer cannot carry OUR number: it picks a colour
    // from InfoviewBuildingData/InfoviewBuildingStatusData, whose BuildingType and
    // BuildingStatusType are closed enums resolved by a hard-coded switch over game
    // components, with no channel a mod can fill. So this system takes the same seam
    // and overwrites the component in between, which is why the ordering attributes
    // below are load-bearing rather than tidy.
    //
    // The value comes from the mod's own 32 m tile grid (TransitArchitectSystem's
    // access field), sampled at the building's position — no per-entity storage and no
    // structural change, and the grid is rebuilt only when the equity pass runs.
    [UpdateAfter(typeof(ObjectColorSystem))]
    [UpdateBefore(typeof(BatchDataSystem))]
    public sealed partial class BuildingAccessColorSystem : GameSystemBase
    {
        private EntityQuery m_BuildingQuery;

#pragma warning disable CS8618 // Assigned in OnCreate, which the ECS lifecycle always
        // runs before OnUpdate. Annotating these nullable would force a null check at
        // every use site for a state in which nothing works anyway.
        private ToolSystem m_ToolSystem;

        private TransitArchitectSystem m_Overlay;
#pragma warning restore CS8618

        // The tile field as the job needs it. Copied from the overlay system's managed
        // array when its version changes, which is once per demand refresh at most.
        private NativeArray<byte> m_Field;

        private int m_FieldVersion = -1;

        private int2 m_Grid;

        private float2 m_WorldMin;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Overlay = World.GetOrCreateSystemManaged<TransitArchitectSystem>();
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadWrite<Color>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Hidden>(),
                },
            });
            RequireForUpdate(m_BuildingQuery);
        }

        protected override void OnDestroy()
        {
            if (m_Field.IsCreated)
            {
                m_Field.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // Only while an infoview is open at all: outside one the game does not
            // consume Game.Objects.Color, and ObjectColorSystem does not run either.
            if (m_ToolSystem.activeInfoview == null)
            {
                return;
            }

            int index = m_Overlay.TransitAccessInfomodeIndex;
            if (index <= 0 || index > byte.MaxValue || !TryTakeField())
            {
                return;
            }

            var job = new ColorBuildingsJob
            {
                m_Field = m_Field,
                m_Grid = m_Grid,
                m_WorldMin = m_WorldMin,
                m_TileSize = Assumptions.TileSize,
                m_Index = (byte)index,
                m_TransformType = SystemAPI.GetComponentTypeHandle<Transform>(isReadOnly: true),
                m_ColorType = SystemAPI.GetComponentTypeHandle<Color>(),
            };
            Dependency = job.ScheduleParallel(m_BuildingQuery, Dependency);
        }

        // Copies the overlay system's field when it has changed. False while the mod
        // has not measured one yet, or while its grid does not match its length —
        // a mismatch means a resize landed between the two, and one frame of vanilla
        // colours is better than sampling off the end of the array.
        private bool TryTakeField()
        {
            byte[]? source = m_Overlay.AccessField;
            int2 grid = m_Overlay.AccessFieldGrid;
            if (source is null || grid.x <= 0 || grid.y <= 0 || source.Length != grid.x * grid.y)
            {
                return false;
            }

            if (m_FieldVersion == m_Overlay.AccessFieldVersion && m_Field.IsCreated && m_Field.Length == source.Length)
            {
                return true;
            }

            if (m_Field.IsCreated && m_Field.Length != source.Length)
            {
                m_Field.Dispose();
            }

            if (!m_Field.IsCreated)
            {
                m_Field = new NativeArray<byte>(source.Length, Allocator.Persistent);
            }

            m_Field.CopyFrom(source);
            m_FieldVersion = m_Overlay.AccessFieldVersion;
            m_Grid = grid;
            m_WorldMin = m_Overlay.AccessFieldWorldMin;
            return true;
        }

        [BurstCompile]
        private struct ColorBuildingsJob : IJobChunk
        {
            [ReadOnly]
            public NativeArray<byte> m_Field;

            public int2 m_Grid;
            public float2 m_WorldMin;
            public float m_TileSize;
            public byte m_Index;

            [ReadOnly]
            public ComponentTypeHandle<Transform> m_TransformType;

            public ComponentTypeHandle<Color> m_ColorType;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Transform> transforms = chunk.GetNativeArray(ref m_TransformType);
                NativeArray<Color> colors = chunk.GetNativeArray(ref m_ColorType);
                for (int i = 0; i < colors.Length; i++)
                {
                    float3 position = transforms[i].m_Position;
                    int x = (int)math.floor((position.x - m_WorldMin.x) / m_TileSize);
                    int z = (int)math.floor((position.z - m_WorldMin.y) / m_TileSize);
                    if (x < 0 || z < 0 || x >= m_Grid.x || z >= m_Grid.y)
                    {
                        continue;
                    }

                    // The sub-colour flag is the game's (lot colouring, destroyed and
                    // under-construction buildings), so it is carried over rather than
                    // cleared.
                    colors[i] = new Color(m_Index, m_Field[(z * m_Grid.x) + x], colors[i].m_SubColor);
                }
            }

            void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
            }
        }
    }
}
