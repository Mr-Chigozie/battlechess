// The kill moves: how each kind of fighter takes a life, up to the killing blow (Capture() in BattleController.cs
// plays the impact, the death and the return). Purely presentation; the rules decided the capture already.
//   Blades   two fast cuts with slash trails, then a finisher: spin, overhead leap, or dash-through
//   Spears   two jabs and a skewering lunge
//   Paws     stalk, pounce, claw left, claw right, maul
//   Guns     bursts with muzzle flash, brass and every round landing (Machines fire blaster bolts)
//   Heavy    shells and missiles on a smoking arc into an explosion that throws the victim
//   Sniper   prone, a laser on the head, time slows, one round
//   Spells   energy gathers at the staff, the orb flies and bursts
//   Ninja    three spinning shuriken
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChessWars.Game
{
    public partial class BattleController
    {
        enum Kill { Blade, Spear, Paws, Charge, Slam, Guns, Blaster, Heavy, Sniper, Spell, Shuriken, Beam, Lightning }
        enum Finisher { None, Spin, Overhead, DashThrough }

        // What the killing blow needs from Capture().
        Coroutine killFinishing;     // still running after impact (a beam) and must end before moving on
        bool killExplode, killLaunch, killHeadshot;
        float killPush;              // how far the victim reels from the final blow

        static readonly HashSet<string> Beasts = new HashSet<string> { "undead", "vikings", "spartans", "machines" };   // knights on paws

        static Kill KindOf(UnitView a)
        {
            if (a.Type == 'n' && Beasts.Contains(a.Army)) return Kill.Paws;
            if (a.Army == "marines" && a.Type == 'b') return Kill.Sniper;
            if (a.Army == "samurai" && a.Type == 'b') return Kill.Shuriken;
            if (Heavy(a)) return Kill.Heavy;
            // Where the model's weapon says otherwise than the web game's style table.
            if ((a.Army == "machines" || a.Army == "spartans") && a.Type == 'k') return Kill.Blade;   // energy blade, short sword
            if (a.Army == "marines" && a.Type == 'k') return Kill.Guns;                              // sidearm
            if ((a.Army == "vikings" && a.Type == 'q') || ((a.Army == "samurai" || a.Army == "asgardians") && a.Type == 'p')) return Kill.Spear;
            switch (a.Style)
            {
                case "strike": return Kill.Blade;
                case "thrust": return Kill.Spear;
                case "charge": return Kill.Charge;
                case "slam": return Kill.Slam;
                case "shoot": return a.Army == "machines" ? Kill.Blaster : Kill.Guns;
                case "spell": return Kill.Spell;
                case "beam": return Kill.Beam;
                default: return Kill.Lightning;
            }
        }

        // Tank, Speakeasy barrel, Pagoda cannon, Turret, and the Annihilator's missile pods.
        static bool Heavy(UnitView a) =>
            (a.Type == 'r' && (a.Army == "marines" || a.Army == "mafia" || a.Army == "samurai" || a.Army == "machines"))
            || (a.Army == "machines" && a.Type == 'q');

        static Finisher PickFinisher(UnitView a, Kill kind)
        {
            if (kind != Kill.Blade) return Finisher.None;
            if (a.Army == "samurai") return Finisher.DashThrough;                                  // iaido: one cut, walk on
            if ((a.Type == 'k' || a.Type == 'q') && Random.value < 0.6f) return Finisher.Overhead;  // the great ones strike from above
            return (Finisher)Random.Range(1, 4);
        }

        static readonly Dictionary<Kill, string[]> KillVerbs = new Dictionary<Kill, string[]>
        {
            [Kill.Paws] = new[] { "mauls", "savages", "tears into" },
            [Kill.Sniper] = new[] { "picks off", "drops", "snipes" },
            [Kill.Heavy] = new[] { "blows apart", "shells", "obliterates" },
            [Kill.Shuriken] = new[] { "cuts down", "fells", "pins" },
        };

        static string VerbFor(Kill kind, Finisher finisher, string style)
        {
            if (finisher == Finisher.DashThrough) return "cuts clean through";
            if (KillVerbs.TryGetValue(kind, out var verbs)) return verbs[Random.Range(0, verbs.Length)];
            return Armies.KillVerb(style);
        }

        static WaitForSeconds Wait(float s) => new WaitForSeconds(s);

        // ---------------------------------------------------------------- small hits before the killing blow

        void MiniHit(UnitView v, Vector3 dir, Color color, float push = 0.07f, string sound = "slice")
        {
            var at = v.Center;
            Sfx.Play(sound, at);
            fx.Burst(at, color, 10, 2f, 0.7f, 0.3f);
            fx.Burst(at, Color.white, 5, 1.6f, 0.6f, 0.2f);
            if (blood) fx.Burst(at, Armies.All[v.Army].Blood, 8, 1.8f, 0.8f, 0.4f);
            Fx.Shake(0.1f);
            fx.Hitstop(0.04f);
            StartCoroutine(v.Nudge(dir, push, 0.08f));
            StartCoroutine(v.FlashWhite());
            if (killCam.Active) killCam.PunchIn(0.05f);
        }

        void Slash(UnitView a, float tilt, float from, float to, Color color, float radius = 0.62f, float width = 0.14f, float seconds = 0.12f)
        {
            var forward = a.transform.forward;
            var side = Quaternion.AngleAxis(tilt, forward) * a.transform.right;
            var center = a.transform.position + Vector3.up * a.Height * 0.55f;
            Sfx.Play(width >= 0.18f ? "bigswish" : "swish", center);
            fx.SlashArc(center, forward, side, radius, from, to, Color.Lerp(Color.white, color, 0.45f), width, seconds);
        }

        // ---------------------------------------------------------------- the moves

        IEnumerator KillMove(Kill kind, Finisher finisher, UnitView a, UnitView v, Vector3 dir, Color color)
        {
            killFinishing = null;
            killExplode = killLaunch = killHeadshot = false;
            killPush = 0.16f;
            var A = a.transform.position;
            var V = v.transform.position;
            float dist = Vector3.Distance(A, V);
            switch (kind)
            {
                case Kill.Blade:
                    // Close fast, two cuts (forehand, backhand), then the finisher.
                    yield return a.Travel(V - dir * 0.62f, seconds: 0.18f + dist * 0.22f);
                    StartCoroutine(a.StrikeIn(0.16f));
                    yield return Wait(0.1f);
                    Slash(a, 35f, 85f, -85f, color);
                    yield return Wait(0.06f);
                    MiniHit(v, dir, color);
                    yield return Wait(0.16f);
                    StartCoroutine(a.StrikeIn(0.16f));
                    yield return Wait(0.1f);
                    Slash(a, -35f, -85f, 85f, color);
                    yield return Wait(0.06f);
                    MiniHit(v, dir, color);
                    yield return Wait(0.18f);
                    switch (finisher)
                    {
                        case Finisher.Spin:
                            StartCoroutine(a.Spin(0.3f));
                            StartCoroutine(a.StrikeIn(0.3f));
                            yield return Wait(0.18f);
                            Slash(a, 0f, -200f, 160f, color, 0.72f, 0.18f, 0.14f);
                            yield return Wait(0.12f);
                            killPush = 0.3f;
                            break;
                        case Finisher.Overhead:
                            StartCoroutine(a.Hop(0.5f, 0.42f));
                            StartCoroutine(a.StrikeIn(0.42f));
                            yield return Wait(0.3f);
                            Slash(a, 90f, -100f, 60f, color, 0.7f, 0.2f, 0.1f);   // over the top and down
                            yield return Wait(0.12f);
                            fx.Shockwave(v.transform.position, color);
                            fx.Dust(v.transform.position + Vector3.up * 0.1f, 10);
                            Sfx.Play("slam", v.transform.position, 0.55f, 1.2f);
                            break;
                        default:
                            // Dash-through: one stroke on the way past; the victim stands a heartbeat, then falls.
                            a.Play("Attack", 0.04f, UnitView.AttackHit - 0.08f);
                            Sfx.Play("bigswish", a.Center, 1f, 1.15f);
                            yield return a.Travel(V + dir * 0.8f, seconds: 0.12f, walk: false, turn: false);
                            fx.Cut(v.Center, Quaternion.LookRotation(dir).eulerAngles.y + 90f);
                            Sfx.Play("slice", v.Center);
                            Sfx.Play("clang", v.Center, 0.5f, 1.3f);
                            fx.Burst(v.Center, Color.white, 14, 2.6f, 0.7f, 0.3f);
                            Fx.Shake(0.12f);
                            yield return new WaitForSecondsRealtime(0.45f);
                            killPush = 0.05f;
                            break;
                    }
                    break;

                case Kill.Spear:
                    yield return a.Travel(V - dir * 0.72f, seconds: 0.18f + dist * 0.22f);
                    for (int i = 0; i < 2; i++)
                    {
                        StartCoroutine(a.StrikeIn(0.12f));
                        yield return a.Nudge(dir, 0.12f, 0.08f, true);
                        StartCoroutine(fx.Beam(a.Center + dir * 0.2f, v.Center, Color.Lerp(Color.white, color, 0.4f), 0.12f));
                        MiniHit(v, dir, color, 0.05f, "stab");
                        yield return a.Nudge(dir, -0.12f, 0.12f);
                        yield return Wait(0.08f);
                    }
                    // Draw back, then drive it home.
                    yield return a.Nudge(dir, -0.15f, 0.2f);
                    Sfx.Play("whoosh", a.Center, 0.8f, 1.3f);
                    StartCoroutine(a.StrikeIn(0.12f));
                    yield return a.Nudge(dir, 0.45f, 0.12f, true);
                    Sfx.Play("stab", v.Center, 1f, 0.85f);
                    killPush = 0.4f;
                    break;

                case Kill.Paws:
                {
                    var claw = Color.Lerp(Color.white, color, 0.5f);
                    // Stalk in, pounce, rake left, rake right, then maul.
                    Sfx.Play("growl", a.Center);
                    if (dist > 1.6f) yield return a.Travel(V - dir * 1.3f, seconds: 0.2f + dist * 0.2f);
                    Sfx.Play("roar", a.Center);
                    StartCoroutine(a.StrikeIn(0.45f));
                    yield return a.Travel(V - dir * 0.55f, arc: 0.7f, seconds: 0.45f, walk: false, turn: false);
                    fx.Dust(a.transform.position + Vector3.up * 0.1f, 8);
                    fx.ClawMarks(v.Center, dir, -50f, claw);
                    Sfx.Play("land", a.transform.position, 0.6f);
                    MiniHit(v, dir, color, sound: "claw");
                    yield return Wait(0.2f);
                    StartCoroutine(a.StrikeIn(0.14f));
                    yield return Wait(0.14f);
                    fx.ClawMarks(v.Center, dir, 50f, claw);
                    MiniHit(v, dir, color, sound: "claw");
                    yield return Wait(0.14f);
                    StartCoroutine(v.Shake(0.45f));
                    Sfx.Play("growl", v.Center, 1f, 1.2f);
                    StartCoroutine(a.Nudge(dir, 0.15f, 0.1f));
                    for (int i = 0; i < 3; i++)
                    {
                        fx.Burst(v.Center, blood ? Armies.All[v.Army].Blood : color, 6, 1.6f, 0.7f, 0.3f);
                        Fx.Shake(0.06f);
                        Sfx.Play("hit", v.Center, 0.5f, 1.3f);
                        yield return Wait(0.13f);
                    }
                    StartCoroutine(a.StrikeIn(0.12f));
                    yield return Wait(0.12f);
                    killPush = 0.3f;
                    break;
                }

                case Kill.Charge:
                {
                    yield return a.Nudge(dir, -0.25f, 0.35f);
                    float run = 0.2f + dist * 0.16f;
                    Sfx.Play("gallop", a.Center, 1f, Mathf.Clamp(0.75f / run, 0.9f, 1.6f));
                    StartCoroutine(a.StrikeIn(run));
                    yield return a.Travel(V - dir * 0.45f, seconds: run, accelerate: true, walk: false, turn: false);
                    killLaunch = true;
                    break;
                }

                case Kill.Slam:
                    StartCoroutine(a.StrikeIn(1.0f));
                    yield return Wait(0.3f);
                    Sfx.Play("whoosh", a.Center, 1f, 0.8f);
                    yield return a.Travel(V, arc: 1.7f, seconds: 0.7f, walk: false, turn: false);
                    break;

                case Kill.Guns:
                case Kill.Blaster:
                {
                    bool bolts = kind == Kill.Blaster;
                    bool spray = (a.Army == "marines" || a.Army == "mafia") && a.Type == 'q';   // machine gun, Tommy gun
                    int shots = spray ? 8 : 3;
                    var flash = bolts ? color : Armies.Hex(0xffd27a);
                    a.Play("Shoot");
                    yield return Wait(0.22f);
                    Coroutine last = null;
                    for (int i = 0; i < shots; i++)
                    {
                        var tip = a.Tip;
                        var target = v.Center + Random.insideUnitSphere * 0.08f;
                        fx.MuzzleFlash(tip, dir, flash);
                        Sfx.Play(bolts ? "blaster" : spray ? "tommy" : "gun", tip);
                        if (!bolts) fx.Casing(tip - dir * 0.1f, a.transform.right);
                        if (!bolts && i % 2 == 0) Sfx.Play("casing", tip, 1f, 1f, 0.3f);
                        last = StartCoroutine(Round(bolts ? Fx.Shot.Bolt : Fx.Shot.Tracer, tip, target, color, v, dir, i == shots - 1));
                        StartCoroutine(a.Nudge(dir, -0.015f, 0.04f));
                        Fx.Shake(0.03f);
                        yield return Wait(spray ? 0.07f : 0.17f);
                    }
                    yield return last;
                    break;
                }

                case Kill.Heavy:
                {
                    bool missiles = a.Type == 'q';
                    int rounds = a.Army == "machines" ? (missiles ? 3 : 2) : 1;
                    a.Play("Shoot");
                    yield return Wait(0.3f);
                    Coroutine last = null;
                    for (int i = 0; i < rounds; i++)
                    {
                        var tip = a.Tip + Vector3.up * 0.1f;
                        fx.MuzzleFlash(tip, dir, Armies.Hex(0xffb347), 1.8f);
                        Sfx.Play(missiles ? "missile" : "cannon", tip);
                        fx.Dust(tip, 6);
                        StartCoroutine(a.Nudge(dir, -0.12f, 0.08f));
                        Fx.Shake(0.15f);
                        last = StartCoroutine(fx.Fire(missiles ? Fx.Shot.Missile : Fx.Shot.Shell, tip, v.Center + Random.insideUnitSphere * 0.1f, color));
                        yield return Wait(0.18f);
                    }
                    yield return last;
                    killExplode = killLaunch = true;
                    break;
                }

                case Kill.Sniper:
                {
                    // Down on his belly, the laser finds the head, the world holds its breath, one round.
                    var red = Armies.Hex(0xff2a2a);
                    a.Play("Shoot");
                    yield return Wait(0.72f);
                    a.SetAnimSpeed(0f);
                    var head = v.transform.position + Vector3.up * v.Height * 0.85f;
                    var laser = fx.Laser(a.ProneTip, head, red);
                    var hum = Sfx.Play("laser", head);
                    if (killCam.Active) killCam.PunchIn(0.08f);
                    for (float t = 0; t < 1.0f; t += Time.deltaTime)
                    {
                        laser.SetPosition(0, a.ProneTip);
                        laser.widthMultiplier = 0.022f + Random.value * 0.01f;
                        fx.Glow(head, red, 0.16f, 0.05f);
                        yield return null;
                    }
                    Destroy(laser.gameObject);
                    if (hum != null) hum.Stop();
                    a.SetAnimSpeed(1f);
                    yield return Wait(0.1f);
                    fx.MuzzleFlash(a.ProneTip, dir, Armies.Hex(0xffd27a), 2f);
                    Sfx.Play("sniper", a.ProneTip);
                    Fx.Shake(0.22f);
                    fx.Hitstop(0.02f, 0.25f, 0.45f);   // bullet time for the round's flight
                    yield return fx.Fire(Fx.Shot.Sniper, a.ProneTip, head, color);
                    fx.Burst(head, Color.white, 16, 2.8f, 0.8f, 0.4f);
                    Sfx.Play("bullethit", head, 1f, 0.8f);
                    killHeadshot = true;
                    killPush = 0.35f;
                    yield return Wait(0.3f);
                    fx.Casing(a.ProneTip - dir * 0.3f, a.transform.right);   // work the bolt
                    Sfx.Play("bolt", a.ProneTip);
                    Sfx.Play("casing", a.ProneTip, 1f, 0.9f, 0.35f);
                    break;
                }

                case Kill.Spell:
                    a.Play("Shoot");
                    yield return fx.ChargeUp(() => a.Tip, color, 0.7f);
                    Sfx.Play("orb", a.Tip);
                    yield return fx.Fire(Fx.Shot.Orb, a.Tip, v.Center, color);
                    killExplode = true;
                    break;

                case Kill.Shuriken:
                {
                    a.Play("Shoot");
                    yield return Wait(0.25f);
                    Coroutine last = null;
                    for (int i = 0; i < 3; i++)
                    {
                        Sfx.Play("shuriken", a.Tip);
                        last = StartCoroutine(Round(Fx.Shot.Shuriken, a.Tip, v.Center + Random.insideUnitSphere * 0.1f, color, v, dir, i == 2));
                        yield return Wait(0.13f);
                    }
                    yield return last;
                    break;
                }

                case Kill.Beam:
                    a.Play("Shoot");
                    yield return fx.ChargeUp(() => a.Tip, color, 0.45f);
                    Sfx.Play("zap", a.Tip);
                    killFinishing = StartCoroutine(fx.Beam(a.Tip, v.Center, color, 0.9f));
                    yield return Wait(0.35f);
                    break;

                default:   // Lightning
                    a.Play("Shoot");
                    yield return fx.ChargeUp(() => a.Tip, color, 0.4f);
                    hud.Flash(0.5f);
                    Sfx.Play("thunder", v.Center);
                    Sfx.Play("zap", a.Tip, 0.6f, 1.3f);
                    StartCoroutine(fx.Lightning(a.Tip, v.Center, color, 4));
                    yield return fx.Lightning(V + Vector3.up * 7f, v.Center, color, 5);
                    break;
            }
        }

        // One round in flight; all but the last land as hits the victim staggers from.
        IEnumerator Round(Fx.Shot shot, Vector3 from, Vector3 to, Color color, UnitView v, Vector3 dir, bool killing)
        {
            yield return fx.Fire(shot, from, to, color);
            if (killing) yield break;
            Sfx.Play(shot == Fx.Shot.Shuriken ? "thunk" : "bullethit", to);
            fx.Burst(to, shot == Fx.Shot.Bolt ? color : Armies.Hex(0xffd27a), 5, 1.5f, 0.5f, 0.2f);
            if (blood) fx.Burst(to, Armies.All[v.Army].Blood, 4, 1.2f, 0.7f, 0.3f);
            StartCoroutine(v.Nudge(dir, 0.035f, 0.05f));
            Fx.Shake(0.05f);
        }
    }
}
