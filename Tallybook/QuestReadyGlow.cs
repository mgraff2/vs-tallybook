using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Tallybook
{
    /// <summary>
    /// A gold shimmer over a villager once you are carrying everything they asked for — the
    /// familiar "this NPC is ready for you" flag, so a finished errand is visible in the
    /// world instead of only in the list.
    ///
    /// Particles rather than a custom renderer: they are drawn, lit and culled by the game,
    /// which means no render-stage bookkeeping and nothing to go wrong at a distance or
    /// through walls. Emitted in small bursts on a timer instead of per frame — the effect is
    /// a gentle shimmer, and per-frame spawning would both look like a firework and cost
    /// real work every frame for something that changes once a quest.
    ///
    /// Purely cosmetic and entirely local: nothing here touches the pin list, the recount, or
    /// the server. If it fails, the errand still completes exactly as before.
    /// </summary>
    public class QuestReadyGlow
    {
        const double Range = 48;      // far enough to spot across a village square
        const int IntervalMs = 400;

        readonly ICoreClientAPI capi;
        readonly TallybookConfig config;
        readonly TallyService svc;
        long tickId;

        public QuestReadyGlow(ICoreClientAPI capi, TallybookConfig config, TallyService svc)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
            tickId = capi.Event.RegisterGameTickListener(OnTick, IntervalMs);
        }

        public void Dispose()
        {
            if (tickId != 0)
            {
                capi.Event.UnregisterGameTickListener(tickId);
                tickId = 0;
            }
        }

        void OnTick(float dt)
        {
            try
            {
                if (!config.QuestReadyGlow) return;

                var ready = svc.ReadyQuestGivers();
                if (ready.Count == 0) return;

                var eye = capi.World?.Player?.Entity?.Pos?.XYZ;
                if (eye == null) return;

                var found = capi.World.GetEntitiesAround(eye, (float)Range, (float)Range,
                    e => e != null && e.Alive && IsNamed(e, ready));
                if (found == null) return;

                foreach (var npc in found) Emit(npc);
            }
            catch (Exception e)
            {
                // Cosmetic only — never let it interrupt play. Stop rather than log every tick.
                capi.Logger.Warning("[tallybook] quest glow disabled after error: {0}", e.Message);
                config.QuestReadyGlow = false;
            }
        }

        static bool IsNamed(Entity e, HashSet<string> names)
        {
            try { return names.Contains(e.GetName()); }
            catch { return false; }
        }

        void Emit(Entity npc)
        {
            var pos = npc.Pos?.XYZ;
            if (pos == null) return;

            // Just above the head, using the entity's own height so it sits correctly on a
            // tall trader and a short villager alike.
            double top = npc.SelectionBox?.Y2 ?? 1.8f;
            var centre = new Vec3d(pos.X, pos.Y + top + 0.35, pos.Z);

            var props = new SimpleParticleProperties(
                1, 3,
                Color,
                new Vec3d(centre.X - 0.25, centre.Y, centre.Z - 0.25),
                new Vec3d(centre.X + 0.25, centre.Y + 0.3, centre.Z + 0.25),
                new Vec3f(-0.05f, 0.08f, -0.05f),
                new Vec3f(0.05f, 0.2f, 0.05f),
                1.4f,      // lifetime
                -0.02f,    // drifts gently upward rather than falling
                0.12f, 0.28f,
                EnumParticleModel.Quad);

            props.WindAffected = false;
            props.ShouldDieInAir = false;
            props.SelfPropelled = true;

            capi.World.SpawnParticles(props, null);
        }

        /// <summary>
        /// Config hex to the packed colour the particle system wants. Kept in one place so
        /// that if the channel order ever reads wrong on screen, exactly one line changes.
        /// </summary>
        int Color
        {
            get
            {
                var c = TallybookConfig.ParseColor(config.QuestReadyGlowColor)
                        ?? new[] { 1.0, 0.75, 0.24, 1.0 };
                return ColorUtil.ToRgba(255, (int)(c[0] * 255), (int)(c[1] * 255), (int)(c[2] * 255));
            }
        }
    }
}
