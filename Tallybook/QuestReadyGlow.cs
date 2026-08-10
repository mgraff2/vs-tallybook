using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Tallybook
{
    /// <summary>
    /// A gold shimmer over a villager who is ready for you: you are carrying everything they
    /// asked for, or they owe you a reward you have not collected — the familiar "this NPC
    /// is ready for you" flag, so a finished errand is visible in the world instead of only
    /// in the list.
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
        readonly QuestHistory history;
        long tickId;

        public QuestReadyGlow(ICoreClientAPI capi, TallybookConfig config, TallyService svc,
                              QuestHistory history)
        {
            this.capi = capi;
            this.config = config;
            this.svc = svc;
            this.history = history;
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

        /// <summary>Session-only error latch. It must never touch the saved config: a
        /// cosmetic hiccup that silently rewrote the player's settings would keep the glow
        /// off forever, with the Options switch showing why nowhere.</summary>
        bool disabled;

        void OnTick(float dt)
        {
            try
            {
                if (disabled || !config.QuestReadyGlow) return;

                // Case-insensitive: the name the glow looks for can come from a dialogue
                // *filename* ("beata" → "Beata") while the entity carries its own casing.
                var ready = new HashSet<string>(svc.ReadyQuestGivers(), StringComparer.OrdinalIgnoreCase);

                // The other half of "ready for you": a quest handed in whose reward is
                // still waiting glows over whoever pays it out — which is not always who
                // took the goods (Kat takes Beata's bread; Beata owes for it).
                if (history != null)
                {
                    foreach (var waiting in history.AwaitingRewards())
                    {
                        if (waiting.Giver != null) ready.Add(waiting.Giver);
                    }
                }
                if (ready.Count == 0) return;

                var eye = capi.World?.Player?.Entity?.Pos?.XYZ;
                if (eye == null) return;

                // LoadedEntities, never GetEntitiesAround: the partition query has returned
                // empty here before while entities stood in plain sight, and a glow that
                // silently never appears looks exactly like the feature being broken.
                // Distance first — it is a couple of subtractions, while GetName can be a
                // lang lookup, and this runs for every loaded entity.
                var entities = capi.World.LoadedEntities;
                if (entities == null) return;

                foreach (var npc in entities.Values)
                {
                    if (npc == null || !npc.Alive) continue;

                    var pos = npc.Pos?.XYZ;
                    if (pos == null || pos.SquareDistanceTo(eye) > Range * Range) continue;
                    if (!IsNamed(npc, ready)) continue;

                    Emit(npc);
                }
            }
            catch (Exception e)
            {
                // Cosmetic only — never let it interrupt play. Stop rather than log every
                // tick, but in memory only: the config file is the player's, not an error log.
                capi.Logger.Warning("[tallybook] quest glow disabled after error: {0}", e.Message);
                disabled = true;
            }
        }

        /// <summary>
        /// Everything `.tallybook glow` needs to answer "why is there no orb": the option,
        /// the error latch, both halves of the ready set, and every conversable NPC in range
        /// with whether its name matches. The name comparison is the usual suspect — the
        /// ready set can carry a dialogue filename's casing while the entity has its own.
        /// </summary>
        public List<string> Diagnose()
        {
            var lines = new List<string>
            {
                "option: " + (config.QuestReadyGlow ? "on" : "OFF (Options screen)")
                    + (disabled ? " — stopped this session after an error, see client-main.log" : "")
            };

            var ready = new HashSet<string>(svc.ReadyQuestGivers(), StringComparer.OrdinalIgnoreCase);
            lines.Add("ready hand-ins: " + (ready.Count == 0 ? "none" : string.Join(", ", ready)));

            var waiting = history?.AwaitingRewards() ?? new List<(string, string, string)>();
            if (waiting.Count == 0) lines.Add("awaiting reward: none");
            foreach (var w in waiting)
            {
                lines.Add($"awaiting reward: {w.Item2} — paid out by "
                    + (w.Item3 ?? "unknown (no dialogue file sets its reward flag)"));
                if (w.Item3 != null) ready.Add(w.Item3);
            }

            var eye = capi.World?.Player?.Entity?.Pos?.XYZ;
            int seen = 0;
            if (eye != null && capi.World.LoadedEntities != null)
            {
                foreach (var e in capi.World.LoadedEntities.Values)
                {
                    if (e == null || !e.Alive) continue;
                    var pos = e.Pos?.XYZ;
                    if (pos == null || pos.SquareDistanceTo(eye) > Range * Range) continue;
                    if (e.GetBehavior<EntityBehaviorConversable>() == null) continue;

                    string name = null;
                    try { name = e.GetName(); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;

                    seen++;
                    lines.Add($"nearby: \"{name}\" {Math.Sqrt(pos.SquareDistanceTo(eye)):0}m away — "
                        + (ready.Contains(name) ? "should be glowing" : "no quest ready"));
                }
            }
            if (seen == 0) lines.Add("nearby: no conversable NPC within " + (int)Range + " blocks");
            return lines;
        }

        /// <summary>A visible answer to "does the particle render at all": one burst over
        /// your own head, independent of every quest condition above.</summary>
        public void TestBurst()
        {
            var me = capi.World?.Player?.Entity;
            if (me != null) Emit(me);
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
            // A touch of glow so the sparkle reads in a dark attic too — the orb below is
            // the light source of the effect, these are its embers.
            props.VertexFlags = 128;

            capi.World.SpawnParticles(props, null);

            // The orb at the heart of the sparkle (Mark): one large, still, fullbright
            // particle per burst, placed mid-band and living slightly longer than the burst
            // interval, so consecutive bursts overlap into a single steady light rather
            // than a blink. VertexFlags carries the glow bits (verified by reflection,
            // 1.22): 255 renders it as a light, not a lit thing.
            var orbAt = new Vec3d(centre.X, centre.Y + 0.15, centre.Z);
            var orb = new SimpleParticleProperties(
                1, 1,
                Color,
                orbAt, orbAt,
                new Vec3f(), new Vec3f(),
                IntervalMs / 1000f + 0.15f,
                0f,        // hangs where it is
                0.4f, 0.4f,
                EnumParticleModel.Quad);

            orb.WindAffected = false;
            orb.ShouldDieInAir = false;
            orb.VertexFlags = 255;

            capi.World.SpawnParticles(orb, null);
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
