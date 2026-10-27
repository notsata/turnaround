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
