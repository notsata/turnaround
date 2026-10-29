using System.Collections;
using System.Linq;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Company;
using Turnaround.Tasks;
using Turnaround.Equipment;
using Turnaround.Events;
using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.Vehicles;
using UnityEngine;

namespace Turnaround.Core
{
    // Dev/QA only, enabled with "-host -autoturn": plays one whole turnaround as the host.
    // Toggles go through the real interaction path (walk up, press the button, Rpc, host validation);
    // loose items are just set down in place, which the host may do because it owns the physics.
    public class DevAutoTurn : MonoBehaviour
    {
        PlayerController me;
        PlayerInteraction hands;

        static void Log(string msg) => Debug.Log("[AUTOTURN] " + msg);

        IEnumerator Start()
        {
            while (PlayerController.Local == null) yield return null;
            me = PlayerController.Local;
            hands = me.GetComponent<PlayerInteraction>();
            yield return LobbyTest();
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-stresstest") >= 0)
            {
                yield return StressTest();
                yield break;
            }
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-testdrive") >= 0)
            {
                yield return DriveTest();
                yield break;
            }
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-testbelt") >= 0)
            {
                yield return BeltLoaderTest();
                yield break;
            }

            AircraftController ac = null;
            while (ac == null || ac.State != AircraftState.Parked)
            {
                ac = FindFirstObjectByType<AircraftController>();
                yield return null;
            }
            yield return new WaitForSeconds(1.5f);
            Log("aircraft parked, starting turn");
            RandomEventManager.Instance.ServerTriggerAll(); // worst shift ever: every complication at once
            yield return new WaitForSeconds(1f);

            var chocks = Items(CarryKind.Chock);
            var cones = Items(CarryKind.Cone);
            int nextChock = 0, nextCone = 0;
            foreach (var zone in ac.Zones)
                for (int r = 0; r < zone.required; r++)
                {
                    var item = zone.kind == CarryKind.Chock ? chocks[nextChock++] : cones[nextCone++];
                    Place(item.transform, zone.transform.position + ac.transform.forward * (r - 0.5f) * 1.2f);
                }

            var cargoDoor = ac.Doors.First(d => d.isCargoDoor);
            yield return Use(cargoDoor.transform.position, cargoDoor);

            var gpuPort = ac.Services.First(s => s.type == ServiceType.GroundPower);
            var gpu = GroundPowerUnit.Instance;
            var cable = Items(CarryKind.GpuCable)[0];
            Place(gpu.GetComponentInParent<Rigidbody>().transform, ac.transform.position + ac.transform.right * 3f);
            Place(cable.transform, gpu.cableOrigin.position + ac.transform.right * 0.8f);
            yield return new WaitForSeconds(0.5f);
            yield return Use(cable.transform.position, cable);
            yield return Use(gpuPort.transform.position, gpuPort);
            for (int tries = 0; tries < 4 && !gpu.Running; tries++) yield return Use(gpu.transform.position, gpu); // slap, then switch on
            Log($"gpu powering={gpu.IsPowering(ac)}");

            foreach (var sp in ac.Services.Where(s => s.type != ServiceType.GroundPower))
                for (int tries = 0; tries < 100 && !sp.IsConnected; tries++) // the fuel truck is "on its way"
                {
                    yield return Use(sp.transform.position, sp);
                    if (!sp.IsConnected) yield return new WaitForSeconds(1f);
                }

            var bags = BaggageManager.Instance;
            var zoneCenter = bags.inboundZone.transform.position;
            int n = 0;
            foreach (var bag in BaggageItem.All.Where(b => b.Inbound).ToList())
                Place(bag.transform, zoneCenter + new Vector3(n % 6 - 2.5f, n / 36 * 0.5f, n++ / 6 % 6 - 2.5f));
            yield return new WaitForSeconds(1f);
            var hold = ac.Holds[0];
            n = 0;
            foreach (var bag in BaggageItem.All.Where(b => !b.Inbound && b.Flight == ac.FlightNumber).ToList())
            {
                bag.transform.rotation = hold.transform.rotation * Quaternion.Euler(0f, 90f, 0f); // same layout as the inbound spawn
                Place(bag.transform, hold.SpawnPoint(n++), 0f);
            }
            Log($"bags moved: {n} outbound into hold");

            var victim = FindObjectsByType<PlayerController>(FindObjectsSortMode.None).FirstOrDefault(p => p != me && p.Vehicle == null);
            var missile = BaggageCart.All.FirstOrDefault(c => !c.IsHitched && c.displayName == "Baggage Cart");
            if (victim != null && missile != null)
            {
                Vector3 dir = victim.transform.position - missile.transform.position;
                dir.y = 0f;
                missile.transform.rotation = Quaternion.LookRotation(dir);
                Place(missile.transform, victim.transform.position - dir.normalized * 5f, 0.1f);
                missile.GetComponent<Rigidbody>().linearVelocity = dir.normalized * 7f;
                Log("runaway cart launched at the other crew member");
                yield return new WaitForSeconds(2f);
            }
            var stray = BaggageItem.All.FirstOrDefault(b => !b.Inbound && b.Flight != ac.FlightNumber);
            if (stray != null)
            {
                yield return Use(stray.transform.position, stray.GetComponent<CarryableObject>());
                if (hands.Held != null) hands.Held.ThrowRpc(me.transform.forward + Vector3.up * 0.3f, 1f);
                yield return new WaitForSeconds(0.5f);
            }

            while (ac.Services.Any(s => s.requiredForDeparture && !s.IsComplete)) yield return null;
            foreach (var sp in ac.Services.Where(s => s.type != ServiceType.GroundPower))
                yield return Use(sp.transform.position, sp);
            yield return Use(cargoDoor.transform.position, cargoDoor);
            yield return Use(gpuPort.transform.position, gpuPort);
            yield return Use(gpu.transform.position, gpu);
            foreach (var cone in cones) Place(cone.transform, new Vector3(-44f, 0.5f, 26f));
            Log($"services done, doors closed, state={ac.State}");

            var tug = PushbackTug.Instance;
            var tugVehicle = tug.GetComponent<VehicleController>();
            var tugRb = tug.GetComponent<Rigidbody>();
            yield return Use(tugVehicle.seatAnchor.position, tugVehicle, tugVehicle.exitPoint.position); // board from where a player would stand
            tug.transform.rotation = Quaternion.LookRotation(-ac.transform.forward);
            Place(tug.transform, ac.transform.position + ac.transform.forward * 3.4f, 0.1f);
            yield return new WaitForSeconds(0.5f);
            Log($"tow point {Vector3.Distance(tug.towPoint.position, ac.transform.position):F2} m from nose gear, driver={tugVehicle.Driver != null}, " +
                $"seated={me.Vehicle == tugVehicle}, stationary={ac.IsStationary}, prompt={tugVehicle.Action.GetActionPrompt(false)}");
            tugVehicle.Action.DoAction(false);
            yield return new WaitForSeconds(0.5f);
            Log($"tug connected={tug.IsConnected}");

            foreach (var chock in chocks) Place(chock.transform, new Vector3(-38f, 0.5f, 23f));
            while (ac.State != AircraftState.ReadyForPush || ac.ChocksPresent > 0) yield return null;
            tugVehicle.Action.DoAction(true);
            yield return new WaitForSeconds(0.5f);
            Log($"pushback requested, state={ac.State}");

            float started = Time.time;
            while (!ac.PastPushLimit && Time.time - started < 120f) // lightning may still have the ramp closed
            {
                tugVehicle.SendInput(1f, 0f, false); // tug faces the aircraft: driving forward pushes it back
                yield return null;
            }
            tugVehicle.SendInput(0f, 0f, true);
            yield return new WaitForSeconds(1.5f);
            Log($"pushed to z={ac.transform.position.z:F1} heading={ac.transform.eulerAngles.y:F0}");
            tugVehicle.Action.DoAction(false);
            yield return new WaitForSeconds(0.5f);
            tugVehicle.RequestExit();
            yield return new WaitForSeconds(0.5f);
            Place(tug.transform, new Vector3(-2f, 0.1f, 11f), 0.1f);
            tugRb.linearVelocity = Vector3.zero;

            while (ac.State != AircraftState.Gone) yield return null;
            Log("aircraft gone");

            // Business loop: spend some of the money, then start the next flight the way the host's button does.
            yield return new WaitForSeconds(2f);
            var company = CompanyManager.Instance;
            Log($"company after turn: ${company.State.money}, rep {company.State.reputation}, day {company.State.day}");
            company.BuyEquipmentRpc("ExtraCart");
            company.BuyUpgradeRpc("BetterBreakRoom");
            yield return new WaitForSeconds(1f);
            Log($"after shopping: ${company.State.money}, extra carts {company.State.extraCarts}, upgrades [{string.Join(",", company.State.upgrades)}], " +
                $"carts on ramp {BaggageCart.All.Count(c => c.displayName == "Baggage Cart")}");

            TurnaroundManager.Instance.ServerStartNextFlight();
            Log($"next flight started: state={ac.State}, flight {ac.FlightNumber}, report cleared={TurnaroundManager.Instance.Report.Length == 0}, bags left={BaggageItem.All.Count(b => b.Flight != 0)}");
            while (ac.State != AircraftState.Parked) yield return null;
            yield return new WaitForSeconds(2f);
            Log($"second flight parked: {TurnaroundManager.Instance.Airline} {ac.FlightNumber}, inbound {bags.InboundTotal}, outbound {bags.OutboundTotal}, " +
                $"tasks complete {TaskManager.Instance.CompletedCount}, door open={cargoDoor.IsOpen}");
            yield return BankruptcyTest(ac);
        }

        // Before the shift: in the lobby, sit down, then the host presses the big red button and everyone goes to the ramp.
        IEnumerator LobbyTest()
        {
            while (ShiftManager.Instance == null) yield return null;
            yield return new WaitForSeconds(1f);
            Log($"lobby: inLobby={ShiftManager.InLobby} me at {me.transform.position}");
            var chair = FindFirstObjectByType<Chair>();
            yield return Use(chair.transform.position, chair);
            yield return new WaitForSeconds(0.5f);
            Log($"chair: sitting={me.Sitting} at {me.transform.position} (seat {chair.seat.position})");
            var button = FindFirstObjectByType<StartShiftButton>();
            yield return Use(button.transform.position, button, button.transform.position + new Vector3(0f, -1.02f, -1.2f));
            yield return new WaitForSeconds(1.5f);
            Log($"shift started: inLobby={ShiftManager.InLobby} sitting={me.Sitting} me at {me.transform.position}");
        }

        // Bankruptcy: everything goes back where the first shift found it, and everyone back to the lobby.
        IEnumerator BankruptcyTest(AircraftController ac)
        {
            var tractor = VehicleController.All.First(v => v.kind == Company.EquipmentKind.Tractor);
            Vector3 home = tractor.transform.position;
            Place(tractor.transform, home + new Vector3(15f, 0f, 20f));
            var chock = Items(CarryKind.Chock)[0];
            Vector3 chockHome = chock.transform.position;
            Place(chock.transform, new Vector3(5f, 0.5f, 40f));
            yield return new WaitForSeconds(1f);
            var company = CompanyManager.Instance;
            company.NewCompanyRpc();
            yield return new WaitForSeconds(2f);
            Log($"bankrupt: inLobby={ShiftManager.InLobby} me at {me.transform.position}, money ${company.State.money}, contracts {company.State.contracts.Count}, " +
                $"tractor {Vector3.Distance(tractor.transform.position, home):F2} m from home, chock {Vector3.Distance(chock.transform.position, chockHome):F2} m, " +
                $"aircraft {ac.State} hold={ac.HoldArrival}, bags {BaggageItem.All.Count}, carts {BaggageCart.All.Count}, report empty={TurnaroundManager.Instance.Report.Length == 0}");
            ShiftManager.Instance.ServerStartShift();
            yield return new WaitForSeconds(2f);
            Log($"after restart: inLobby={ShiftManager.InLobby} me at {me.transform.position}");
        }

        // Repro for "vehicles fly away when moving forward and turning" and "can't drive back from the runway":
        // every vehicle does full-lock circles on the apron and on the airfield, then drives from the runway to the ramp.
        IEnumerator DriveTest()
        {
            yield return new WaitForSeconds(2f);
            foreach (var v in VehicleController.All.ToArray())
            {
                var rb = v.GetComponent<Rigidbody>();
                foreach (var (label, start, yaw, steer, seconds) in new[]
                {
                    ("apron circles", new Vector3(20f, 0f, 45f), 0f, 1f, 8f),
                    ("airfield circles", new Vector3(0f, 0f, 160f), 90f, -1f, 8f),
                    ("runway to ramp", new Vector3(0f, 0f, 150f), 180f, 0f, 25f),
                })
                {
                    v.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                    Place(v.transform, start, 0.05f);
                    yield return new WaitForSeconds(0.5f);
                    yield return Use(v.seatAnchor.position, v, v.exitPoint.position);
                    float maxY = -9f, minY = 9f, maxVy = 0f;
                    for (float t = 0; t < seconds; t += Time.deltaTime)
                    {
                        v.SendInput(1f, steer, false);
                        maxY = Mathf.Max(maxY, rb.position.y);
                        minY = Mathf.Min(minY, rb.position.y);
                        maxVy = Mathf.Max(maxVy, rb.linearVelocity.y);
                        yield return null;
                    }
                    v.SendInput(0f, 0f, true);
                    yield return new WaitForSeconds(1f);
                    v.RequestExit();
                    yield return new WaitForSeconds(0.6f);
                    Log($"{v.displayName}, {label}: y {minY:F2}..{maxY:F2}, max rise {maxVy:F2} m/s, ended at {rb.position}");
                }
            }
            Log("drive test done");
        }

        // Repro for "the belt loader flies away when you get in": board it and watch it for a few seconds.
        IEnumerator BeltLoaderTest()
        {
            yield return new WaitForSeconds(2f);
            var loader = FindObjectsByType<VehicleController>(FindObjectsSortMode.None).First(v => v.kind == EquipmentKindBelt);
            var body = loader.GetComponent<Rigidbody>();
            Log($"loader before: y={body.position.y:F3} vy={body.linearVelocity.y:F2}");
            yield return Use(loader.seatAnchor.position, loader, loader.exitPoint.position);
            for (int i = 0; i < 16; i++)
            {
                Log($"  +{i * 0.25f:F2}s loader y={body.position.y:F3} vy={body.linearVelocity.y:F2} driver={loader.HasDriver} " +
                    $"player y={me.transform.position.y:F2} ccEnabled={me.GetComponent<CharacterController>().enabled}");
                yield return new WaitForSeconds(0.25f);
            }
        }

        // ---------- physics stress test: everything that used to launch, bury or fling things ----------

        float maxHeight;
        string maxWho;
        readonly System.Collections.Generic.HashSet<string> warned = new();

        void Track()
        {
            foreach (var v in VehicleController.All) Note(v.transform.position.y, v.displayName);
            foreach (var c in BaggageCart.All) Note(c.transform.position.y, $"{c.displayName} {c.NetworkObjectId} at {c.transform.position} hitched={c.IsHitched}");
        }

        void Note(float y, string who)
        {
            if (y > maxHeight + 0.5f) Log($"   new max height {y:F2}: {who} at t={Time.time:F1}");
            if (y > maxHeight) (maxHeight, maxWho) = (y, who);
            if (y < -1f && warned.Add(who.Split(' ')[0] + who.Split(' ')[1])) Log($"!! {who} is underground at y={y:F2}");
        }

        IEnumerator Watch(float seconds)
        {
            for (float t = 0; t < seconds; t += Time.deltaTime)
            {
                Track();
                yield return null;
            }
        }

        IEnumerator StressTest()
        {
            yield return new WaitForSeconds(3f);
            var tractor = VehicleController.All.First(v => v.kind == Company.EquipmentKind.Tractor);
            var tractorRb = tractor.GetComponent<Rigidbody>();
            var cart = BaggageCart.All.First(c => c.displayName == "Baggage Cart");

            // 0. Ram the parked equipment row: the old way carts ended up stacked on things.
            tractor.transform.rotation = Quaternion.LookRotation(Vector3.left);
            Place(tractor.transform, new Vector3(-22f, 0f, 17f), 0.05f);
            yield return new WaitForSeconds(0.3f);
            yield return Use(tractor.seatAnchor.position, tractor, tractor.exitPoint.position);
            for (float t = 0; t < 5f; t += Time.deltaTime)
            {
                tractor.SendInput(1f, 0f, false);
                Track();
                yield return null;
            }
            tractor.SendInput(0f, 0f, true);
            yield return Watch(2f);
            tractor.RequestExit();
            yield return new WaitForSeconds(0.6f);
            Log($"0. rammed the parked row at full speed: max height {maxHeight:F2} ({maxWho})");

            // 1. Hitch/unhitch the same cart 20 times right behind the tractor, out in open space.
            cart.transform.rotation = Quaternion.identity;
            Place(cart.transform, new Vector3(-20f, 0f, 40f), 0.05f);
            tractor.transform.rotation = Quaternion.identity;
            Place(tractor.transform, cart.tongueTip.position + Vector3.forward * 3f, 0.05f);
            yield return new WaitForSeconds(1f);
            int hitched = 0;
            for (int i = 0; i < 20; i++)
            {
                yield return Use(cart.transform.position, cart, cart.transform.position + cart.transform.right * 1.8f); // beside it, like a person
                if (cart.IsHitched) hitched++;
                yield return Watch(0.2f);
            }
            Log($"1. hitch toggles: {hitched}/10 hitched states seen, tractor y={tractor.transform.position.y:F2}, max height so far {maxHeight:F2} ({maxWho})");

            // 2. Tow it in tight circles at full speed, then slam the brakes.
            if (!cart.IsHitched) yield return Use(cart.transform.position, cart);
            yield return Use(tractor.seatAnchor.position, tractor, tractor.exitPoint.position);
            for (float t = 0; t < 6f; t += Time.deltaTime)
            {
                tractor.SendInput(1f, t < 3f ? 1f : -1f, false);
                Track();
                yield return null;
            }
            tractor.SendInput(0f, 0f, true);
            yield return Watch(2f);
            Log($"2. towing: hitched={cart.IsHitched}, tongue-to-tractor {Vector3.Distance(cart.tongueTip.position, tractor.transform.position):F1} m, max height {maxHeight:F2} ({maxWho})");

            // 3. Ram the parked aircraft at full throttle: fuselage side, main gear, tail.
            AircraftController ac = null;
            while (ac == null || !ac.IsStationary)
            {
                ac = FindFirstObjectByType<AircraftController>();
                yield return null;
            }
            tractor.RequestExit();
            yield return new WaitForSeconds(0.5f);
            if (cart.IsHitched) yield return Use(cart.transform.position, cart);
            yield return Use(tractor.seatAnchor.position, tractor, tractor.exitPoint.position);
            var runs = new[] { (new Vector3(14f, 0f, -5f), new Vector3(0f, 0f, -5f), "fuselage"), (new Vector3(12f, 0f, -10.5f), new Vector3(0f, 0f, -10.5f), "main gear"), (new Vector3(0f, 0f, -38f), new Vector3(0f, 0f, -15f), "tail") };
            foreach (var (from, to, name) in runs)
            {
                Vector3 start = ac.transform.TransformPoint(from), target = ac.transform.TransformPoint(to);
                tractor.transform.rotation = Quaternion.LookRotation(target - start);
                Place(tractor.transform, start, 0.05f);
                tractorRb.linearVelocity = Vector3.zero;
                yield return new WaitForSeconds(0.3f);
                for (float t = 0; t < 5f; t += Time.deltaTime)
                {
                    tractor.SendInput(1f, 0f, false);
                    Track();
                    yield return null;
                }
                tractor.SendInput(0f, 0f, true);
                Vector3 inPlane = ac.transform.InverseTransformPoint(tractor.transform.position);
                bool inside = Mathf.Abs(inPlane.x) < 1.2f && inPlane.z < 3f && inPlane.z > -21f;
                Log($"3. rammed {name}: tractor ended {(inside ? "INSIDE the fuselage" : "outside")} at local ({inPlane.x:F1},{inPlane.y:F1},{inPlane.z:F1}), max height {maxHeight:F2}");
                yield return new WaitForSeconds(0.5f);
            }
            tractor.RequestExit();
            yield return new WaitForSeconds(0.6f);

            // 4. Rapid full-power throws while looking straight down (used to fling the thrower).
            me.cameraPivot.localRotation = Quaternion.Euler(85f, 0f, 0f);
            var bag = Items(CarryKind.Cone)[0];
            Vector3 throwSpot = me.transform.position;
            float worstDrift = 0f;
            for (int i = 0; i < 15; i++)
            {
                Place(bag.transform, me.transform.position + me.transform.forward * 1.2f);
                yield return new WaitForSeconds(0.15f);
                bag.Interact(hands);
                yield return new WaitForSeconds(0.15f);
                if (hands.Held != null) hands.Held.ThrowRpc(me.cameraPivot.forward, 1f);
                yield return new WaitForSeconds(0.25f);
                worstDrift = Mathf.Max(worstDrift, Vector3.Distance(me.transform.position, throwSpot));
            }
            me.cameraPivot.localRotation = Quaternion.identity;
            Log($"4. 15 downward throws: thrower moved at most {worstDrift:F2} m, knocked down={me.GetComponent<PlayerRagdoll>().IsDown}");

            // 5. Hammer the other crew member repeatedly; they must always get back up above ground.
            var victim = PlayerController.All.FirstOrDefault(p => p != me);
            var hammer = Items(CarryKind.Tool).First(t => t.displayName == "Sledgehammer");
            yield return Use(hammer.transform.position, hammer);
            var melee = hammer.GetComponent<MeleeWeapon>();
            int hits = 0;
            for (int i = 0; i < 4 && victim != null; i++)
            {
                while (victim.GetComponent<PlayerRagdoll>().IsDown) yield return null; // let them get back up first
                yield return new WaitForSeconds(0.6f);
                Vector3 at = victim.transform.position - victim.transform.forward * 1.3f;
                me.Teleport(new Vector3(at.x, victim.transform.position.y, at.z), Quaternion.LookRotation(Vector3.ProjectOnPlane(victim.transform.position - at, Vector3.up)));
                me.cameraPivot.localRotation = Quaternion.identity;
                yield return new WaitForSeconds(0.3f);
                melee.TrySwing(me.cameraPivot);
                yield return new WaitForSeconds(1.2f);
                if (victim.GetComponent<PlayerRagdoll>().IsDown) hits++;
            }
            yield return new WaitForSeconds(4f);
            Log($"5. hammer: {hits}/4 swings knocked the victim down; victim now down={victim?.GetComponent<PlayerRagdoll>().IsDown}, y={victim?.transform.position.y:F2}");

            // 6. Shove unattended equipment: walking bumps barely move it, the hammer nudges it, nothing flies.
            var gpu = BaggageCart.All.First(c => c.kind == Company.EquipmentKind.Gpu);
            Log($"   GPU candidates: {string.Join(", ", BaggageCart.All.Select(c => $"{c.displayName}/{c.kind}/{c.NetworkObjectId}"))}");
            tractor.transform.rotation = Quaternion.identity; // open space, nothing in front
            Place(tractor.transform, new Vector3(-10f, 0f, 30f), 0.05f);
            gpu.transform.rotation = Quaternion.identity;
            Place(gpu.transform, new Vector3(-4f, 0f, 30f), 0.05f);
            yield return new WaitForSeconds(1f);
            Vector3 tractorStart = tractor.transform.position, gpuStart = gpu.transform.position;
            for (int i = 0; i < 25; i++) // along the wheels; sideways the tyres (rightly) won't budge
            {
                tractor.Push(tractor.transform.forward * 4.5f);
                gpu.Push(gpu.transform.forward * 4.5f);
                yield return Watch(0.25f);
            }
            Log($"6a. 25 walking shoves: tractor moved {Vector3.Distance(tractorStart, tractor.transform.position):F2} m, GPU moved {Vector3.Distance(gpuStart, gpu.transform.position):F2} m (GPU {gpu.NetworkObjectId} {gpuStart} -> {gpu.transform.position}, hitched={gpu.IsHitched})");
            Vector3 beforeHammer = tractor.transform.position;
            for (int i = 0; i < 4; i++)
            {
                Vector3 at = tractor.transform.position - tractor.transform.forward * 2.6f;
                me.Teleport(new Vector3(at.x, 0.1f, at.z), Quaternion.LookRotation(Vector3.ProjectOnPlane(tractor.transform.forward, Vector3.up)));
                yield return new WaitForSeconds(0.2f);
                melee.TrySwing(me.cameraPivot);
                yield return Watch(1.2f);
            }
            Log($"6b. 4 hammer hits: tractor moved {Vector3.Distance(beforeHammer, tractor.transform.position):F2} m; overall max height {maxHeight:F2} ({maxWho})");
            Log("stress test done");
        }

        const Company.EquipmentKind EquipmentKindBelt = Company.EquipmentKind.BeltLoader;

        static CarryableObject[] Items(CarryKind kind) =>
            FindObjectsByType<CarryableObject>(FindObjectsSortMode.InstanceID).Where(c => c.kind == kind).ToArray();

        static void Place(Transform t, Vector3 position, float lift = 0.3f)
        {
            t.position = position + Vector3.up * lift;
            if (t.TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic) rb.linearVelocity = Vector3.zero;
            Physics.SyncTransforms();
        }

        // Stand on the ground next to the target and press E on it.
        IEnumerator Use(Vector3 target, IInteractable thing, Vector3? standAt = null)
        {
            Vector3 away = new Vector3(target.x, 0f, target.z) - new Vector3(me.transform.position.x, 0f, me.transform.position.z);
            Vector3 stand = standAt ?? new Vector3(target.x, 0.1f, target.z) - (away.sqrMagnitude > 0.01f ? away.normalized : Vector3.forward) * 1.2f;
            me.Teleport(stand, Quaternion.LookRotation(new Vector3(target.x - stand.x, 0f, target.z - stand.z) + Vector3.forward * 0.001f));
            yield return new WaitForSeconds(0.3f);
            Log($"use {thing.GetPrompt(hands) ?? "(no prompt)"}");
            thing.Interact(hands);
            yield return new WaitForSeconds(0.4f);
        }
    }
}
