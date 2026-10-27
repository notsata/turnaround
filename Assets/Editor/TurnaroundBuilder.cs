using System;
using System.Collections.Generic;
using System.Linq;
using Turnaround.Aircraft;
using Turnaround.Baggage;
using Turnaround.Company;
using Turnaround.Equipment;
using Turnaround.Events;
using Turnaround.Vehicles;
using Turnaround.Core;
using Turnaround.Interaction;
using Turnaround.Player;
using Turnaround.Tasks;
using Turnaround.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = System.Random;

// Generates every placeholder asset and the Ramp scene from code. Safe to re-run: TURNAROUND > Rebuild Prototype.
public static class TurnaroundBuilder
{
    const string ScenePath = "Assets/Scenes/Ramp.unity";
    static readonly Color FogColor = new(0.62f, 0.66f, 0.7f);

    static readonly Dictionary<string, Material> Mats = new();
    static Font font;
    static Material textMat;
    static PhysicsMaterial bouncy;
    static PhysicsMaterial slide;

    [MenuItem("TURNAROUND/Rebuild Prototype")]
    public static void Build()
    {
        foreach (var dir in new[] { "Assets/Materials", "Assets/Textures" }) AssetDatabase.DeleteAsset(dir);
        foreach (var dir in new[] { "Materials", "Textures", "Prefabs", "Prefabs/Aircraft", "Prefabs/Equipment", "Prefabs/Players", "Prefabs/Props", "Scenes", "ScriptableObjects" })
        {
            var full = "Assets/" + dir;
            if (!AssetDatabase.IsValidFolder(full))
                AssetDatabase.CreateFolder(full.Substring(0, full.LastIndexOf('/')), full.Substring(full.LastIndexOf('/') + 1));
        }
        Mats.Clear();

        PlayerSettings.productName = "TURNAROUND";
        PlayerSettings.runInBackground = true; // two local instances must both keep ticking
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;

        font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        textMat = new Material(Shader.Find("Turnaround/WorldText")) { mainTexture = font.material.mainTexture };
        AssetDatabase.CreateAsset(textMat, "Assets/Materials/WorldText.mat");
        bouncy = new PhysicsMaterial("Luggage") { bounciness = 0.35f, dynamicFriction = 0.5f, staticFriction = 0.6f, bounceCombine = PhysicsMaterialCombine.Maximum };
        AssetDatabase.CreateAsset(bouncy, "Assets/Materials/Luggage.physicMaterial");
        // Vehicles do their own grip, so their bodies glide on the concrete.
        slide = new PhysicsMaterial("VehicleSlide") { dynamicFriction = 0f, staticFriction = 0f, frictionCombine = PhysicsMaterialCombine.Minimum };
        AssetDatabase.CreateAsset(slide, "Assets/Materials/VehicleSlide.physicMaterial");

        var player = BuildPlayerPrefab();
        var props = BuildPropPrefabs();
        var flight = CreateFlightDefinition();
        var aircraft = BuildAircraftPrefab(CreateAircraftDefinition(), flight);
        var equipment = BuildEquipmentPrefabs();
        var bagManager = BuildBaggageManagerPrefab(props);
        var turnaround = BuildTurnaroundPrefab(flight, CreateTasks(), equipment["BaggageCart"]);

        const string listPath = "Assets/ScriptableObjects/NetworkPrefabs.asset";
        AssetDatabase.DeleteAsset(listPath);
        var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
        list.Add(new NetworkPrefab { Prefab = player });
        foreach (var p in props.Values) list.Add(new NetworkPrefab { Prefab = p });
        list.Add(new NetworkPrefab { Prefab = aircraft });
        foreach (var e in equipment.Values) list.Add(new NetworkPrefab { Prefab = e });
        list.Add(new NetworkPrefab { Prefab = turnaround });
        list.Add(new NetworkPrefab { Prefab = bagManager }); // without scene management, in-scene network objects must be registered prefabs
        AssetDatabase.CreateAsset(list, listPath);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var world = new GameObject("World").transform;
        BuildLighting();
        BuildRamp(world);
        PlaceProps(props);
        var aircraftInstance = PlaceAircraft(aircraft);
        PlaceEquipment(equipment);
        PlaceBaggageManager(bagManager, aircraftInstance);
        PrefabUtility.InstantiatePrefab(turnaround);

        var spawn = new GameObject("PlayerSpawn").transform;
        spawn.SetPositionAndRotation(new Vector3(-15f, 0.1f, 9f), Quaternion.identity);

        var menuCam = new GameObject("MenuCamera");
        menuCam.transform.SetPositionAndRotation(new Vector3(42f, 16f, 58f), Quaternion.LookRotation(new Vector3(-6f, 0f, 8f) - new Vector3(42f, 16f, 58f)));
        SetupCamera(menuCam.AddComponent<Camera>());
        menuCam.AddComponent<AudioListener>();
        menuCam.AddComponent<LowResRenderer>();

        var nmGo = new GameObject("NetworkManager");
        var nm = nmGo.AddComponent<NetworkManager>();
        var transport = nmGo.AddComponent<UnityTransport>();
        nm.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            PlayerPrefab = player,
            ConnectionApproval = true,
            EnableSceneManagement = false, // single scene; both sides already have it loaded
        };
        nm.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);

        new GameObject("GameUI").AddComponent<GameUI>().menuCamera = menuCam;

        EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        GenerateNetworkHashes(new List<GameObject>(props.Values) { player, aircraft, bagManager, turnaround }.Concat(equipment.Values).ToList());
        Debug.Log("[TURNAROUND] Prototype rebuilt: " + ScenePath);
    }

    // NGO only assigns GlobalObjectIdHash in OnValidate, and only once the prefab/scene exists on disk
    // (scene must also be in the build list). Objects created from code never get that call, so force it.
    static void GenerateNetworkHashes(List<GameObject> prefabs)
    {
        var onValidate = typeof(NetworkObject).GetMethod("OnValidate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        foreach (var prefab in prefabs)
        {
            onValidate.Invoke(prefab.GetComponent<NetworkObject>(), null);
            PrefabUtility.SavePrefabAsset(prefab);
        }
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.OpenScene(ScenePath);
        foreach (var no in Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            onValidate.Invoke(no, null);
            if (no.PrefabIdHash == 0) Debug.LogError($"[TURNAROUND] {no.name} has no network hash");
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }

    [MenuItem("TURNAROUND/Build Windows Player")]
    public static void BuildWindowsPlayer()
    {
        // release.ps1 passes "-buildVersion 1.2.3"; that's what the in-game updater compares against GitHub releases.
        var args = Environment.GetCommandLineArgs();
        int v = Array.IndexOf(args, "-buildVersion");
        if (v >= 0 && v + 1 < args.Length) PlayerSettings.bundleVersion = args[v + 1];
        var report = BuildPipeline.BuildPlayer(new[] { ScenePath }, "Builds/Windows/TURNAROUND.exe", BuildTarget.StandaloneWindows64, BuildOptions.None);
        Debug.Log("[TURNAROUND] Player build: " + report.summary.result);
        if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded) EditorApplication.Exit(1);
    }

    // ---------- materials ----------

    // Crude 64x64 texture: base color with blotchy variation and dark speckles. Point filtered on purpose.
    static Material Mat(string name, Color color, float variation = 0.08f, float speckle = 0.04f, float tiling = 1f)
    {
        if (Mats.TryGetValue(name, out var existing)) return existing;
        var rng = new Random(name.GetHashCode());
        var tex = new Texture2D(64, 64, TextureFormat.RGBA32, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat, name = name };
        var px = new Color[64 * 64];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            float blotch = Mathf.PerlinNoise(x * 0.09f + name.Length, y * 0.09f) - 0.5f;
            float v = 1f + blotch * variation * 2f + ((float)rng.NextDouble() - 0.5f) * variation;
            if (rng.NextDouble() < speckle) v *= 0.6f;
            px[y * 64 + x] = new Color(color.r * v, color.g * v, color.b * v, 1f);
        }
        tex.SetPixels(px);
        tex.Apply();
        AssetDatabase.CreateAsset(tex, $"Assets/Textures/{name}.asset");

        var mat = new Material(Shader.Find("Standard")) { mainTexture = tex, color = Color.white, mainTextureScale = Vector2.one * tiling };
        mat.SetFloat("_Glossiness", 0.05f);
        AssetDatabase.CreateAsset(mat, $"Assets/Materials/{name}.mat");
        return Mats[name] = mat;
    }

    static Material Flat(string name, Color c) => Mat(name, c, 0.03f, 0f);

    // ---------- primitive helpers ----------

    static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool collider = false, Vector3 euler = default)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = mat;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat, Vector3 euler = default) =>
        Prim(PrimitiveType.Cube, name, parent, pos, size, mat, true, euler);

    // Flat paint/marking on the ground, no collider so nobody trips on a stripe.
    static void Paint(string name, Transform parent, Vector3 center, Vector2 size, Material mat, float y = 0.011f) =>
        Prim(PrimitiveType.Cube, name, parent, new Vector3(center.x, y, center.z), new Vector3(size.x, 0.02f, size.y), mat);

    static Transform Group(string name, Transform parent, Vector3 pos = default, float yaw = 0f)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = Quaternion.Euler(0f, yaw, 0f);
        return t;
    }

    // height = approximate cap height in metres.
    static void Text(string text, Transform parent, Vector3 pos, Vector3 euler, float height, Color color)
    {
        var go = new GameObject("Text_" + text.Split('\n')[0]);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 64;
        tm.characterSize = height * 10f / 64f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        go.GetComponent<MeshRenderer>().sharedMaterial = textMat;
    }

    // Hollow box room with a door gap on local +Z, a roof, and one sad fluorescent light.
    static Transform Room(string name, Transform parent, Vector3 pos, float yaw, Vector2 size, float door, string sign)
    {
        var wall = Mat("PaintedBlock", new Color(0.72f, 0.7f, 0.62f), 0.1f, 0.05f, 2f);
        var r = Group(name, parent, pos, yaw);
        float h = 3.2f, t = 0.25f, w = size.x, d = size.y, side = (w - door) / 2f;
        Box("WallBack", r, new Vector3(0, h / 2, -d / 2), new Vector3(w, h, t), wall);
        Box("WallLeft", r, new Vector3(-w / 2, h / 2, 0), new Vector3(t, h, d), wall);
        Box("WallRight", r, new Vector3(w / 2, h / 2, 0), new Vector3(t, h, d), wall);
        Box("WallFrontL", r, new Vector3(-w / 2 + side / 2, h / 2, d / 2), new Vector3(side, h, t), wall);
        Box("WallFrontR", r, new Vector3(w / 2 - side / 2, h / 2, d / 2), new Vector3(side, h, t), wall);
        Box("Lintel", r, new Vector3(0, h - 0.3f, d / 2), new Vector3(door, 0.6f, t), wall);
        Box("Roof", r, new Vector3(0, h + 0.1f, 0), new Vector3(w + 0.4f, 0.2f, d + 0.4f), Mat("Roof", new Color(0.35f, 0.36f, 0.37f)));
        Paint("Floor", r, Vector3.zero, new Vector2(w - 0.1f, d - 0.1f), Mat("LinoFloor", new Color(0.55f, 0.58f, 0.5f), 0.12f, 0.08f, 4f), 0.012f);
        Prim(PrimitiveType.Cube, "FluorescentTube", r, new Vector3(0, h - 0.05f, 0), new Vector3(1.4f, 0.06f, 0.2f), Flat("Tube", new Color(0.9f, 1f, 0.92f)));

        var light = new GameObject("FluorescentLight").AddComponent<Light>();
        light.transform.SetParent(r, false);
        light.transform.localPosition = new Vector3(0, h - 0.4f, 0);
        light.type = LightType.Point;
        light.color = new Color(0.82f, 1f, 0.88f);
        light.range = Mathf.Max(w, d) * 0.9f;
        light.intensity = 1.3f;
        light.shadows = LightShadows.None;

        Text(sign, r, new Vector3(0, h - 0.3f, d / 2 + 0.14f), new Vector3(0, 180, 0), 0.32f, new Color(0.1f, 0.1f, 0.12f));
        return r;
    }

    static void SetupCamera(Camera cam)
    {
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = FogColor;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 400f;
        cam.fieldOfView = 72f;
    }

    // ---------- player ----------

    static GameObject BuildPlayerPrefab()
    {
        var skin = Flat("Skin", new Color(0.93f, 0.72f, 0.55f));
        var vest = Mat("HiVis", Color.white, 0.04f, 0.01f); // tinted per player at runtime
        var stripe = Flat("Reflective", new Color(0.85f, 0.87f, 0.9f));
        var shirt = Mat("Shirt", new Color(0.25f, 0.27f, 0.3f));
        var pants = Mat("WorkPants", new Color(0.13f, 0.17f, 0.3f), 0.1f, 0.03f);
        var boot = Mat("Boot", new Color(0.18f, 0.12f, 0.08f), 0.1f, 0.05f);
        var glove = Mat("Glove", new Color(0.8f, 0.62f, 0.3f), 0.12f, 0.05f);
        var muff = Flat("EarMuff", new Color(0.85f, 0.12f, 0.1f));
        var black = Flat("Black", new Color(0.05f, 0.05f, 0.05f));

        var root = new GameObject("Player");
        var cc = root.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0, 0.9f, 0);
        cc.stepOffset = 0.35f;
        root.AddComponent<NetworkObject>();
        var nt = root.AddComponent<NetworkTransform>();
        nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
        nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        var interaction = root.AddComponent<PlayerInteraction>();
        var controller = root.AddComponent<PlayerController>();
        var anim = root.AddComponent<PlayerAnimator>();
        root.AddComponent<PlayerRagdoll>();

        var body = Group("Body", root.transform);
        var torso = Prim(PrimitiveType.Cube, "Torso", body, new Vector3(0, 1.08f, 0), new Vector3(0.58f, 0.62f, 0.36f), vest);
        Prim(PrimitiveType.Cube, "StripeLow", body, new Vector3(0, 0.96f, 0), new Vector3(0.6f, 0.06f, 0.38f), stripe);
        Prim(PrimitiveType.Cube, "StripeHigh", body, new Vector3(0, 1.2f, 0), new Vector3(0.6f, 0.06f, 0.38f), stripe);
        Prim(PrimitiveType.Cube, "Hips", body, new Vector3(0, 0.78f, 0), new Vector3(0.5f, 0.2f, 0.32f), pants);

        var head = Group("Head", body, new Vector3(0, 1.62f, 0));
        Prim(PrimitiveType.Sphere, "Skull", head, Vector3.zero, Vector3.one * 0.64f, skin); // oversized on purpose
        Prim(PrimitiveType.Sphere, "Nose", head, new Vector3(0, -0.03f, 0.31f), new Vector3(0.12f, 0.1f, 0.14f), skin);
        Prim(PrimitiveType.Sphere, "EyeL", head, new Vector3(-0.11f, 0.07f, 0.28f), Vector3.one * 0.07f, black);
        Prim(PrimitiveType.Sphere, "EyeR", head, new Vector3(0.11f, 0.07f, 0.28f), Vector3.one * 0.07f, black);
        Prim(PrimitiveType.Cylinder, "MuffL", head, new Vector3(-0.32f, 0, 0), new Vector3(0.22f, 0.06f, 0.22f), muff, false, new Vector3(0, 0, 90));
        Prim(PrimitiveType.Cylinder, "MuffR", head, new Vector3(0.32f, 0, 0), new Vector3(0.22f, 0.06f, 0.22f), muff, false, new Vector3(0, 0, 90));
        Prim(PrimitiveType.Cube, "MuffBand", head, new Vector3(0, 0.31f, 0), new Vector3(0.68f, 0.05f, 0.08f), muff);

        Transform Arm(string n, float x)
        {
            var pivot = Group(n, body, new Vector3(x, 1.32f, 0));
            Prim(PrimitiveType.Capsule, "Sleeve", pivot, new Vector3(0, -0.4f, 0), new Vector3(0.15f, 0.4f, 0.15f), shirt);
            Prim(PrimitiveType.Cube, "Glove", pivot, new Vector3(0, -0.82f, 0), Vector3.one * 0.17f, glove);
            return pivot;
        }

        Transform Leg(string n, float x)
        {
            var pivot = Group(n, body, new Vector3(x, 0.78f, 0));
            Prim(PrimitiveType.Capsule, "Pant", pivot, new Vector3(0, -0.36f, 0), new Vector3(0.2f, 0.34f, 0.2f), pants);
            Prim(PrimitiveType.Cube, "Boot", pivot, new Vector3(0, -0.71f, 0.07f), new Vector3(0.25f, 0.16f, 0.4f), boot); // chunky
            return pivot;
        }

        anim.body = body;
        anim.head = head;
        anim.armL = Arm("ArmL", -0.37f);
        anim.armR = Arm("ArmR", 0.37f);
        anim.legL = Leg("LegL", -0.14f);
        anim.legR = Leg("LegR", 0.14f);
        anim.vestRenderers = new[] { torso.GetComponent<Renderer>() };

        var pivotCam = Group("CameraPivot", root.transform, new Vector3(0, 1.62f, 0));
        var cam = new GameObject("PlayerCamera").AddComponent<Camera>();
        cam.transform.SetParent(pivotCam, false);
        SetupCamera(cam);
        cam.enabled = false;
        cam.gameObject.AddComponent<AudioListener>().enabled = false;
        cam.gameObject.AddComponent<LowResRenderer>().enabled = false;
        var hold = Group("HoldPoint", pivotCam, new Vector3(0, -0.42f, 0.85f));

        controller.cameraPivot = pivotCam;
        controller.playerCamera = cam;
        interaction.cameraPivot = pivotCam;
        interaction.holdPoint = hold;

        return SavePrefab(root, "Assets/Prefabs/Players/Player.prefab");
    }

    static GameObject SavePrefab(GameObject root, string path)
    {
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    // ---------- props ----------

    static Dictionary<string, GameObject> BuildPropPrefabs()
    {
        var props = new Dictionary<string, GameObject>();
        var handle = Flat("Handle", new Color(0.1f, 0.1f, 0.1f));
        var tag = Flat("BagTag", new Color(0.95f, 0.95f, 0.85f));

        void Bag(string id, string display, CarryWeight w, float mass, Vector3 size, Color c)
        {
            props[id] = Prop(id, display, w, mass, t =>
            {
                Prim(PrimitiveType.Cube, "Shell", t, Vector3.zero, size, Mat("Bag_" + id, c, 0.12f, 0.03f));
                Prim(PrimitiveType.Cube, "Handle", t, new Vector3(0, size.y / 2 + 0.03f, 0), new Vector3(size.x * 0.35f, 0.05f, 0.05f), handle);
                Prim(PrimitiveType.Cube, "Tag", t, new Vector3(size.x * 0.2f, size.y / 2 + 0.02f, size.z / 2 + 0.01f), new Vector3(0.06f, 0.12f, 0.01f), tag);
            }, AddBagData);
        }

        Bag("Suitcase", "Suitcase", CarryWeight.Medium, 15f, new Vector3(0.55f, 0.38f, 0.24f), new Color(0.15f, 0.2f, 0.45f));
        Bag("TinySuitcase", "Tiny Suitcase", CarryWeight.Light, 4f, new Vector3(0.34f, 0.24f, 0.14f), new Color(0.95f, 0.5f, 0.7f));
        Bag("LargeSuitcase", "Large Suitcase", CarryWeight.Heavy, 28f, new Vector3(0.78f, 0.55f, 0.32f), new Color(0.12f, 0.12f, 0.13f));
        Bag("HugeSuitcase", "Comically Huge Suitcase", CarryWeight.Heavy, 45f, new Vector3(1.15f, 0.85f, 0.5f), new Color(0.5f, 0.15f, 0.6f));
        Bag("CardboardBox", "Cardboard Box", CarryWeight.Light, 6f, new Vector3(0.42f, 0.36f, 0.42f), new Color(0.72f, 0.55f, 0.33f));

        props["Duffel"] = Prop("Duffel", "Duffel Bag", CarryWeight.Light, 8f, t =>
        {
            Prim(PrimitiveType.Capsule, "Body", t, Vector3.zero, new Vector3(0.32f, 0.35f, 0.32f), Mat("Bag_Duffel", new Color(0.35f, 0.4f, 0.2f), 0.12f, 0.03f), false, new Vector3(0, 0, 90));
            Prim(PrimitiveType.Cube, "Strap", t, new Vector3(0, 0.17f, 0), new Vector3(0.3f, 0.04f, 0.05f), handle);
            Prim(PrimitiveType.Cube, "Tag", t, new Vector3(0.1f, 0.2f, 0.12f), new Vector3(0.06f, 0.12f, 0.01f), tag);
        }, AddBagData);

        var orange = Mat("ConeOrange", new Color(1f, 0.38f, 0.05f), 0.1f, 0.05f);
        var white = Flat("ConeWhite", new Color(0.92f, 0.92f, 0.9f));
        props["Cone"] = Prop("Cone", "Safety Cone", CarryWeight.Light, 1.2f, t =>
        {
            Prim(PrimitiveType.Cube, "Base", t, new Vector3(0, -0.3f, 0), new Vector3(0.4f, 0.04f, 0.4f), orange);
            Prim(PrimitiveType.Cylinder, "Low", t, new Vector3(0, -0.18f, 0), new Vector3(0.3f, 0.1f, 0.3f), orange);
            Prim(PrimitiveType.Cylinder, "Stripe", t, new Vector3(0, 0.0f, 0), new Vector3(0.22f, 0.08f, 0.22f), white);
            Prim(PrimitiveType.Cylinder, "Top", t, new Vector3(0, 0.18f, 0), new Vector3(0.13f, 0.1f, 0.13f), orange);
        }, Kind(CarryKind.Cone));

        var yellow = Mat("ChockYellow", new Color(0.95f, 0.8f, 0.1f), 0.15f, 0.08f);
        props["Chock"] = Prop("Chock", "Wheel Chock", CarryWeight.Medium, 8f, t =>
        {
            Prim(PrimitiveType.Cube, "Block", t, Vector3.zero, new Vector3(0.5f, 0.14f, 0.22f), yellow);
            Prim(PrimitiveType.Cube, "Wedge", t, new Vector3(0, 0.06f, 0.04f), new Vector3(0.5f, 0.12f, 0.18f), yellow, false, new Vector3(35, 0, 0));
            Prim(PrimitiveType.Cube, "Rope", t, new Vector3(0.3f, 0.02f, 0), new Vector3(0.12f, 0.03f, 0.03f), Flat("Rope", new Color(0.1f, 0.3f, 0.9f)));
        }, Kind(CarryKind.Chock));

        return props;
    }

    static void AddBagData(GameObject bag)
    {
        bag.AddComponent<BaggageItem>().tagRenderer = bag.transform.Find("Tag").GetComponent<Renderer>();
        bag.GetComponent<CarryableObject>().kind = CarryKind.Bag;
    }

    static Action<GameObject> Kind(CarryKind kind) => go => go.GetComponent<CarryableObject>().kind = kind;

    static GameObject Prop(string id, string displayName, CarryWeight weight, float mass, Action<Transform> buildVisual, Action<GameObject> configure = null)
    {
        var root = new GameObject(id);
        buildVisual(root.transform);

        var bounds = new Bounds(Vector3.zero, Vector3.zero);
        foreach (var r in root.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);
        var col = root.AddComponent<BoxCollider>();
        col.center = bounds.center;
        col.size = bounds.size;
        col.sharedMaterial = bouncy;

        var rb = root.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // thrown bags shouldn't tunnel through walls
        rb.maxDepenetrationVelocity = 2f; // a tight stack settles instead of exploding (and "damaging" itself)

        root.AddComponent<NetworkObject>();
        var nt = root.AddComponent<NetworkTransform>();
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        var carry = root.AddComponent<CarryableObject>();
        carry.displayName = displayName;
        carry.weight = weight;
        configure?.Invoke(root);
        return SavePrefab(root, $"Assets/Prefabs/Props/{id}.prefab");
    }

    static void PlaceProps(Dictionary<string, GameObject> props)
    {
        var parent = new GameObject("Props").transform;
        var rng = new Random(3821);

        void Place(string id, Vector3 pos, float yaw = 0f)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(props[id]);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
        }

        // Untagged luggage that someone definitely dropped. Flight bags spawn when the aircraft parks.
        Place("Suitcase", new Vector3(-20f, 0.4f, 11f), 75f);
        Place("Duffel", new Vector3(6f, 0.4f, 18f), 10f);

        for (int i = 0; i < 6; i++) Place("Cone", new Vector3(-46f + i * 0.9f, 0.4f, 26f), rng.Next(0, 360));
        for (int i = 0; i < 4; i++) Place("Chock", new Vector3(-40f + i * 0.8f, 0.3f, 23f), 90f);
        Place("Cone", new Vector3(14f, 0.4f, 30f), 0f);
    }

    // ---------- environment ----------

    static void BuildLighting()
    {
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = FogColor;
        RenderSettings.fogStartDistance = 30f;
        RenderSettings.fogEndDistance = 170f;

        var sun = new GameObject("OvercastSun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        sun.color = new Color(1f, 0.95f, 0.85f);
        sun.intensity = 0.95f;
        sun.shadows = LightShadows.Hard;
        sun.shadowStrength = 0.55f;
    }

    static void BuildRamp(Transform world)
    {
        var concrete = Mat("DirtyConcrete", new Color(0.56f, 0.56f, 0.54f), 0.14f, 0.06f, 40f);
        var asphalt = Mat("Asphalt", new Color(0.22f, 0.22f, 0.23f), 0.12f, 0.08f, 30f);
        var terminal = Mat("TerminalPanel", new Color(0.6f, 0.62f, 0.66f), 0.08f, 0.03f, 6f);
        var glass = Flat("DirtyGlass", new Color(0.18f, 0.25f, 0.32f));
        var yellow = Mat("FadedYellow", new Color(0.85f, 0.72f, 0.2f), 0.18f, 0.12f, 6f);
        var white = Mat("FadedWhite", new Color(0.82f, 0.82f, 0.78f), 0.15f, 0.12f, 6f);
        var red = Mat("FadedRed", new Color(0.72f, 0.18f, 0.15f), 0.15f, 0.1f, 6f);
        var grass = Mat("SadGrass", new Color(0.38f, 0.45f, 0.24f), 0.2f, 0.1f, 20f);
        var metal = Mat("RustyMetal", new Color(0.45f, 0.42f, 0.38f), 0.2f, 0.12f);
        var wood = Mat("CheapLaminate", new Color(0.6f, 0.48f, 0.32f), 0.1f, 0.03f);
        var plastic = Mat("OfficePlastic", new Color(0.3f, 0.33f, 0.4f), 0.06f, 0.02f);
        var ink = new Color(0.1f, 0.1f, 0.12f);

        // Thick so nothing shoved by an overlap (players, spawns) can be pushed through it.
        Box("Ground", world, new Vector3(0, -1f, 35f), new Vector3(140f, 2f, 140f), concrete);
        Paint("GrassStrip", world, new Vector3(0, 0, 101f), new Vector2(140f, 8f), grass);

        // Terminal building with gates.
        var term = Group("Terminal", world);
        Box("TerminalBlock", term, new Vector3(0, 4.5f, -14f), new Vector3(100f, 9f, 16f), terminal);
        Prim(PrimitiveType.Cube, "WindowStrip", term, new Vector3(0, 6.2f, -5.95f), new Vector3(92f, 1.6f, 0.1f), glass);
        Prim(PrimitiveType.Cube, "RoofEdge", term, new Vector3(0, 9.2f, -14f), new Vector3(100.5f, 0.5f, 16.5f), Mat("Roof", Color.gray));
        foreach (var (gate, x) in new[] { ("B4", 12f), ("B5", 36f) })
        {
            Text("GATE " + gate, term, new Vector3(x, 8.1f, -5.85f), new Vector3(0, 180, 0), 0.9f, new Color(0.95f, 0.8f, 0.2f));
            Prim(PrimitiveType.Cube, "GateDoor" + gate, term, new Vector3(x, 1.3f, -5.95f), new Vector3(2f, 2.6f, 0.1f), Flat("Door", new Color(0.3f, 0.32f, 0.3f)));
        }
        Text("SAFETY FIRST\n(USUALLY)", term, new Vector3(1f, 4.2f, -5.85f), new Vector3(0, 180, 0), 0.45f, new Color(0.9f, 0.25f, 0.2f));

        // Rooms backed against the terminal, doors facing the ramp.
        var bagRoom = Room("BaggageRoom", world, new Vector3(-30f, 0, -2f), 0f, new Vector2(16f, 8f), 6f, "BAGGAGE ROOM");
        Box("InboundBelt", bagRoom, new Vector3(0, 0.45f, -3f), new Vector3(13f, 0.9f, 1.2f), metal);
        Prim(PrimitiveType.Cube, "BeltSurface", bagRoom, new Vector3(0, 0.91f, -3f), new Vector3(12.6f, 0.03f, 1f), Flat("Rubber", new Color(0.08f, 0.08f, 0.08f)));
        Text("OUTBOUND - SORT ME", bagRoom, new Vector3(0, 2.3f, -3.85f), new Vector3(0, 180, 0), 0.25f, ink);

        var breakRoom = Room("BreakRoom", world, new Vector3(-15f, 0, -2f), 0f, new Vector2(8f, 8f), 1.8f, "BREAK ROOM");
        Box("VendingMachine", breakRoom, new Vector3(-3f, 1f, -3.3f), new Vector3(1f, 2f, 0.8f), Mat("VendingRed", new Color(0.7f, 0.12f, 0.1f)));
        Prim(PrimitiveType.Cube, "VendingGlass", breakRoom, new Vector3(-3.1f, 1.2f, -2.89f), new Vector3(0.6f, 1.2f, 0.02f), Flat("VendingGlow", new Color(0.8f, 0.9f, 1f)));
        Box("Table", breakRoom, new Vector3(0.5f, 0.38f, -0.5f), new Vector3(1.8f, 0.76f, 0.9f), wood);
        Box("ChairA", breakRoom, new Vector3(-0.3f, 0.23f, 0.3f), new Vector3(0.45f, 0.46f, 0.45f), plastic);
        Box("ChairB", breakRoom, new Vector3(1.4f, 0.23f, -1.4f), new Vector3(0.45f, 0.46f, 0.45f), plastic, new Vector3(0, 30, 0));
        Box("Microwave", breakRoom, new Vector3(2.8f, 1.05f, -3.5f), new Vector3(0.6f, 0.35f, 0.4f), Flat("Microwave", new Color(0.85f, 0.85f, 0.8f)));
        Box("Counter", breakRoom, new Vector3(2.8f, 0.43f, -3.5f), new Vector3(1.8f, 0.86f, 0.6f), wood);
        Prim(PrimitiveType.Cylinder, "PlantPot", breakRoom, new Vector3(3.3f, 0.2f, 3f), new Vector3(0.35f, 0.2f, 0.35f), Mat("Terracotta", new Color(0.6f, 0.3f, 0.2f)));
        Prim(PrimitiveType.Sphere, "SadPlant", breakRoom, new Vector3(3.3f, 0.6f, 3f), new Vector3(0.5f, 0.6f, 0.5f), Mat("DyingLeaves", new Color(0.4f, 0.45f, 0.2f), 0.25f, 0.2f));
        Text("DAYS WITHOUT\nAN INCIDENT:\n0", breakRoom, new Vector3(1.5f, 2f, -3.85f), new Vector3(0, 180, 0), 0.18f, new Color(0.7f, 0.1f, 0.1f));

        var ops = Room("OperationsOffice", world, new Vector3(-6f, 0, -2f), 0f, new Vector2(7f, 8f), 1.8f, "OPERATIONS");
        Box("Desk", ops, new Vector3(0, 0.38f, -2.5f), new Vector3(2.2f, 0.76f, 0.9f), wood);
        Box("Monitor", ops, new Vector3(0, 1.0f, -2.7f), new Vector3(0.6f, 0.45f, 0.08f), plastic).AddComponent<OfficeComputer>();
        Prim(PrimitiveType.Cube, "Screen", ops, new Vector3(0, 1.0f, -2.65f), new Vector3(0.52f, 0.37f, 0.02f), Flat("CRTBlue", new Color(0.2f, 0.4f, 0.9f)));
        Box("OfficeChair", ops, new Vector3(0.2f, 0.25f, -1.6f), new Vector3(0.5f, 0.5f, 0.5f), plastic, new Vector3(0, 20, 0));
        Box("FilingCabinet", ops, new Vector3(-2.9f, 0.65f, -3.4f), new Vector3(0.5f, 1.3f, 0.6f), metal);
        Text("ON-TIME PERFORMANCE\nIS EVERYONE'S JOB", ops, new Vector3(1.5f, 2.1f, -3.85f), new Vector3(0, 180, 0), 0.13f, ink);

        // Maintenance shed off to the side of equipment parking, door facing the ramp.
        var shed = Room("MaintenanceShed", world, new Vector3(-52f, 0, 40f), 90f, new Vector2(12f, 10f), 4f, "MAINTENANCE");
        Box("Workbench", shed, new Vector3(0, 0.45f, -4.2f), new Vector3(4f, 0.9f, 0.8f), wood);
        Prim(PrimitiveType.Cylinder, "SpareTire", shed, new Vector3(-4f, 0.35f, -3f), new Vector3(0.7f, 0.12f, 0.7f), HandleMat(), true, new Vector3(90, 0, 0));
        Box("OilDrum", shed, new Vector3(4.5f, 0.45f, -3.8f), new Vector3(0.6f, 0.9f, 0.6f), Mat("DrumBlue", new Color(0.15f, 0.3f, 0.6f), 0.2f, 0.1f));

        // Service road between building and stands.
        var road = Group("ServiceRoad", world);
        Paint("Asphalt", road, new Vector3(0, 0, 5f), new Vector2(110f, 5f), asphalt);
        Paint("EdgeNear", road, new Vector3(0, 0, 2.65f), new Vector2(110f, 0.15f), white, 0.013f);
        Paint("EdgeFar", road, new Vector3(0, 0, 7.35f), new Vector2(110f, 0.15f), white, 0.013f);
        for (float x = -52f; x < 55f; x += 6f) Paint("Dash", road, new Vector3(x, 0, 5f), new Vector2(2.5f, 0.15f), white, 0.013f);
        Text("VEHICLE LANE", road, new Vector3(-24f, 0.02f, 5f), new Vector3(90, 0, 0), 0.9f, new Color(0.85f, 0.85f, 0.8f));

        // Gate stands.
        foreach (var (gate, x) in new[] { ("B4", 12f), ("B5", 36f) })
        {
            var stand = Group("Stand" + gate, world, new Vector3(x, 0, 0));
            Paint("LeadIn", stand, new Vector3(0, 0, 37f), new Vector2(0.25f, 46f), yellow, 0.013f);
            Paint("StopBar", stand, new Vector3(0, 0, 14f), new Vector2(3f, 0.3f), yellow, 0.014f);
            Text("RJ-200 STOP", stand, new Vector3(2.8f, 0.02f, 13.4f), new Vector3(90, 0, 0), 0.35f, new Color(0.85f, 0.72f, 0.2f));
            Paint("RestraintL", stand, new Vector3(-11.5f, 0, 27f), new Vector2(0.2f, 36f), red, 0.013f);
            Paint("RestraintR", stand, new Vector3(11.5f, 0, 27f), new Vector2(0.2f, 36f), red, 0.013f);
            Paint("RestraintFront", stand, new Vector3(0, 0, 9f), new Vector2(23f, 0.2f), red, 0.013f);
            Text(gate, stand, new Vector3(0, 0.02f, 52f), new Vector3(90, 0, 0), 3f, new Color(0.85f, 0.72f, 0.2f));
        }

        // Equipment parking.
        var parking = Group("EquipmentParking", world);
        for (int i = 0; i <= 4; i++) Paint("Bay", parking, new Vector3(-48f + i * 4f, 0, 17f), new Vector2(0.15f, 8f), white, 0.013f);
        Paint("BayBack", parking, new Vector3(-40f, 0, 13f), new Vector2(16f, 0.15f), white, 0.013f);
        Text("EQUIPMENT\nPARKING", parking, new Vector3(-40f, 0.02f, 23f), new Vector3(90, 0, 0), 0.7f, new Color(0.85f, 0.85f, 0.8f));

        // Taxiway with a distant aircraft that is not our problem.
        var taxi = Group("Taxiway", world);
        Paint("Asphalt", taxi, new Vector3(0, 0, 85f), new Vector2(140f, 22f), asphalt);
        Paint("Centerline", taxi, new Vector3(0, 0, 85f), new Vector2(140f, 0.3f), yellow, 0.013f);
        var blue = Flat("TaxiLight", new Color(0.2f, 0.4f, 1f));
        for (float x = -65f; x <= 65f; x += 10f)
        {
            Prim(PrimitiveType.Cube, "EdgeLight", taxi, new Vector3(x, 0.15f, 74.5f), Vector3.one * 0.2f, blue);
            Prim(PrimitiveType.Cube, "EdgeLight", taxi, new Vector3(x, 0.15f, 95.5f), Vector3.one * 0.2f, blue);
        }
        DistantJet(world, new Vector3(-30f, 0, 85f), 90f);
        DistantJet(world, new Vector3(64f, 0, 30f), 200f);

        foreach (var x in new[] { -2f, 24f, 48f })
        {
            Box("LightPole", world, new Vector3(x, 6f, 58f), new Vector3(0.3f, 12f, 0.3f), metal);
            Prim(PrimitiveType.Cube, "LightHead", world, new Vector3(x, 12f, 58f), new Vector3(1.4f, 0.3f, 0.6f), metal);
        }
    }

    static Material HandleMat() => Flat("Handle", new Color(0.1f, 0.1f, 0.1f));

    // ---------- turnaround ----------

    static FlightDefinition CreateFlightDefinition()
    {
        const string path = "Assets/ScriptableObjects/Flight3821.asset";
        AssetDatabase.DeleteAsset(path);
        var flight = ScriptableObject.CreateInstance<FlightDefinition>();
        AssetDatabase.CreateAsset(flight, path);
        return flight;
    }

    // Order matters twice: it's the HUD order, and dependencies must come before the tasks that need them.
    static TaskDefinition[] CreateTasks()
    {
        const string dir = "Assets/ScriptableObjects/Tasks";
        AssetDatabase.DeleteAsset(dir);
        AssetDatabase.CreateFolder("Assets/ScriptableObjects", "Tasks");
        var made = new Dictionary<TaskId, TaskDefinition>();

        TaskDefinition T(TaskId id, string name, string description, int score, bool failAtPushback, params TaskId[] deps)
        {
            var t = ScriptableObject.CreateInstance<TaskDefinition>();
            t.id = id;
            t.displayName = name;
            t.description = description;
            t.scoreValue = score;
            t.failIfIncompleteAtPushback = failAtPushback;
            t.dependencies = deps.Select(d => made[d]).ToArray();
            AssetDatabase.CreateAsset(t, $"{dir}/{id}.asset");
            return made[id] = t;
        }

        return new[]
        {
            T(TaskId.InstallChocks, "Install Chocks", "Two chocks at the nose gear.", 100, true),
            T(TaskId.PlaceCones, "Place Cones", "One cone at each wingtip and one at the tail.", 100, true),
            T(TaskId.ConnectGpu, "Connect GPU", "Bring the GPU over, plug its cable into the nose, switch it on.", 150, true, TaskId.InstallChocks),
            T(TaskId.OpenCargoDoor, "Open Cargo Door", "Forward hold, right side.", 50, true, TaskId.InstallChocks),
            T(TaskId.UnloadBags, "Unload Bags", "Inbound bags into the INBOUND area of the bag room.", 250, true, TaskId.OpenCargoDoor),
            T(TaskId.ServiceFuel, "Refuel", "Fuel panel under the right wing.", 150, true, TaskId.InstallChocks),
            T(TaskId.ServiceWater, "Potable Water", "Rear left service panel.", 100, true, TaskId.InstallChocks),
            T(TaskId.ServiceLav, "Lavatory Service", "Rear right service panel. Nobody wants this one.", 100, true, TaskId.InstallChocks),
            T(TaskId.LoadBags, "Load Bags", "Outbound bags for this flight into the hold. Read the tags.", 250, true, TaskId.OpenCargoDoor),
            T(TaskId.CloseCargoDoor, "Close Cargo Door", "After loading.", 50, true, TaskId.LoadBags),
            T(TaskId.DisconnectGpu, "Disconnect GPU", "Unplug before pushback.", 100, true, TaskId.ConnectGpu),
            T(TaskId.RemoveCones, "Remove Cones", "All of them. Yes, the tail one too.", 50, true, TaskId.PlaceCones),
            T(TaskId.ConnectTug, "Connect Pushback Tug", "Drive the tug's tow point to the nose gear, press F.", 100, false),
            T(TaskId.RemoveChocks, "Remove Chocks", "Tug on first, then chocks out.", 50, false, TaskId.ConnectTug),
            T(TaskId.Pushback, "Push Back", "Right-click to ask the captain, push onto the taxiway, F to disconnect.", 200, false, TaskId.RemoveChocks),
            T(TaskId.Depart, "Aircraft Departs", "Drive the tug clear and wave.", 100, false, TaskId.Pushback),
        };
    }

    // Complications: 1-2 per turn, fired somewhere between earliest..latest of the scheduled turn.
    static RandomEventDefinition[] CreateRandomEvents()
    {
        const string dir = "Assets/ScriptableObjects/RandomEvents";
        AssetDatabase.DeleteAsset(dir);
        AssetDatabase.CreateFolder("Assets/ScriptableObjects", "RandomEvents");

        RandomEventDefinition E(RandomEventId id, string name, string speaker, string message, float weight = 1f, float earliest = 0.15f, float latest = 0.7f)
        {
            var e = ScriptableObject.CreateInstance<RandomEventDefinition>();
            e.id = id;
            e.displayName = name;
            e.speaker = speaker;
            e.message = message;
            e.weight = weight;
            e.earliest = earliest;
            e.latest = latest;
            AssetDatabase.CreateAsset(e, $"{dir}/{id}.asset");
            return e;
        }

        return new[]
        {
            E(RandomEventId.LateBags, "Late Bags", "BAG ROOM", "Five connecting bags are coming down. They're on the outbound belt.", 1.5f, 0.45f, 0.75f),
            E(RandomEventId.BrokenGpu, "Broken GPU", "RAMP", "Heads up, the GPU's acting up.", 1f, 0.15f, 0.5f),
            E(RandomEventId.MissingBag, "Missing Bag", "BAG ROOM", "Bag room says you're missing one. Last seen somewhere on the ramp.", 1f, 0.2f, 0.6f),
            E(RandomEventId.OversizedBag, "Oversized Bag", "BAG ROOM", "We have an oversized one for you. It's... large. Outbound belt.", 1f, 0.2f, 0.6f),
            E(RandomEventId.Rain, "Rain", "OPS", "Rain's moving in. Ramp is going to be slick. Drive like you have a license."),
            E(RandomEventId.Lightning, "Lightning", "OPS", "Lightning within five miles. Ramp is CLOSED. Nothing moves.", 0.6f, 0.2f, 0.6f),
            E(RandomEventId.FuelDelay, "Fuel Delay", "FUEL", "Fuel truck is stuck behind catering. Give us a minute. Or several.", 1f, 0.02f, 0.1f),
            E(RandomEventId.RunawayCart, "Runaway Cart", "RAMP", "Who left a cart unbraked? It's rolling.", 1f, 0.2f, 0.7f),
            E(RandomEventId.CaptainImpatient, "Captain Impatient", "COCKPIT", "Ramp, cockpit. Just so you know, we're watching the clock.", 1f, 0.1f, 0.4f),
        };
    }

    // Airlines to work for, kit to buy, and ways to spend money on the company. The first contract is the starter one,
    // and equipment with price 0 is the junk you start with.
    static void CreateCompanyCatalog(CompanyManager company)
    {
        const string dir = "Assets/ScriptableObjects/Company";
        AssetDatabase.DeleteAsset(dir);
        AssetDatabase.CreateFolder("Assets/ScriptableObjects", "Company");
        T Save<T>(T asset, string id) where T : ScriptableObject
        {
            AssetDatabase.CreateAsset(asset, $"{dir}/{typeof(T).Name.Replace("Definition", "")}_{id}.asset");
            return asset;
        }

        ContractDefinition C(string id, string airline, int perDay, float otp, int pay, int tolerance, int rep, int flightBase, string dest)
        {
            var c = ScriptableObject.CreateInstance<ContractDefinition>();
            (c.id, c.airline, c.turnsPerDay, c.minimumOtp, c.paymentPerTurn, c.damageTolerance, c.reputationRequired, c.flightNumberBase, c.destination) =
                (id, airline, perDay, otp, pay, tolerance, rep, flightBase, dest);
            return Save(c, id);
        }
        company.contracts = new[]
        {
            C("RegionalAirways", "Regional Airways", 3, 0.85f, 4200, 1500, 0, 3821, "IND"),
            C("SkyBudget", "SkyBudget", 4, 0.70f, 3300, 3000, 8, 512, "MKE"),
            C("NorthernExecutive", "Northern Executive Air", 2, 0.95f, 6800, 400, 20, 77, "ASE"),
        };

        EquipmentDefinition E(string id, string name, EquipmentKind kind, int price, float reliability, string blurb,
            float maxSpeed = 0f, float accel = 0f, float brake = 0f, float steer = 0f, float belt = 0f)
        {
            var e = ScriptableObject.CreateInstance<EquipmentDefinition>();
            (e.id, e.displayName, e.kind, e.price, e.reliability, e.blurb) = (id, name, kind, price, reliability, blurb);
            (e.maxSpeed, e.acceleration, e.brakeDecel, e.steerResponse, e.beltSpeed) = (maxSpeed, accel, brake, steer, belt);
            return Save(e, id);
        }
        company.equipment = new[]
        {
            E("OldTractor", "Tractor #7", EquipmentKind.Tractor, 0, 0.55f, "Starts on the third try. Brakes are a rumour."),
            E("OldBeltLoader", "Belt Loader (1994)", EquipmentKind.BeltLoader, 0, 0.8f, "Squeaks in a key nobody likes."),
            E("OldTug", "Pushback Tug (hand-me-down)", EquipmentKind.Tug, 0, 0.85f, "Steering is more of a suggestion."),
            E("OldGpu", "Diesel GPU", EquipmentKind.Gpu, 0, 0.7f, "Dies now and then. Responds to violence."),
            E("OldCarts", "Baggage Carts (assorted)", EquipmentKind.Cart, 0, 1f, "Three of them. Mostly square."),
            E("NewTractor", "Voltline E-Tractor", EquipmentKind.Tractor, 6500, 0.97f, "Starts every time. Has brakes. Real ones.", 7.5f, 4.5f, 9f, 7f),
            E("FastBeltLoader", "BeltMaster 3000", EquipmentKind.BeltLoader, 5200, 0.95f, "Belt runs 60% faster. Riding it is still a violation.", 4.5f, belt: 2.6f),
            E("ModernTug", "TowPro 9", EquipmentKind.Tug, 9000, 0.98f, "Steers when you steer. Revolutionary.", 4.5f, 2.5f, 0f, 3f),
            E("ElectricGpu", "Silent Volt GPU", EquipmentKind.Gpu, 4000, 1f, "Never dies. Nobody will need to slap it again."),
            E("ExtraCart", "Baggage Cart (used)", EquipmentKind.Cart, 1200, 1f, "One more cart, parked by the service road."),
        };

        UpgradeDefinition U(string id, string name, UpgradeEffect effect, int price, string blurb)
        {
            var u = ScriptableObject.CreateInstance<UpgradeDefinition>();
            (u.id, u.displayName, u.effect, u.price, u.blurb) = (id, name, effect, price, blurb);
            return Save(u, id);
        }
        company.upgrades = new[]
        {
            U("MaintenanceGarage", "Maintenance Garage", UpgradeEffect.MaintenanceGarage, 7500, "Equipment wears half as fast and repairs cost half."),
            U("BetterBreakRoom", "Better Break Room", UpgradeEffect.BetterBreakRoom, 2500, "+1 reputation every turn. Morale is a KPI now."),
            U("ExtraContractSlot", "Second Contract Desk", UpgradeEffect.ExtraContractSlot, 10000, "Hold two airline contracts at once. More flights, more chaos."),
            U("BetterRadios", "Better Radios", UpgradeEffect.BetterRadios, 3000, "Ops actually tells you things: at most one complication per turn."),
        };
    }

    static GameObject BuildTurnaroundPrefab(FlightDefinition flight, TaskDefinition[] tasks, GameObject cartPrefab)
    {
        var go = new GameObject("Turnaround");
        go.AddComponent<NetworkObject>();
        go.AddComponent<TurnaroundManager>().flight = flight;
        go.AddComponent<TaskManager>().tasks = tasks;
        go.AddComponent<RadioManager>();
        go.AddComponent<SafetyViolationManager>();
        go.AddComponent<RandomEventManager>().events = CreateRandomEvents();
        var company = go.AddComponent<CompanyManager>();
        CreateCompanyCatalog(company);
        company.cartPrefab = cartPrefab;
        company.extraCartSpots = new[] { new Vector3(-46f, 0.1f, 10.5f), new Vector3(-42f, 0.1f, 10.5f), new Vector3(-38f, 0.1f, 10.5f) };
        return SavePrefab(go, "Assets/Prefabs/Turnaround.prefab");
    }

    // Ground power unit: a towable/pushable cart (same physics as a baggage cart) with a power switch and a cable reel.
    static GameObject BuildGpu()
    {
        var (root, _) = PhysicsRoot("GPU", 500f);
        var t = root.transform;
        var green = Mat("GpuGreen", new Color(0.35f, 0.45f, 0.3f), 0.2f, 0.12f);
        SlideBody("Chassis", t, new Vector3(0, 0.25f, 0), new Vector3(1.1f, 0.44f, 1.5f), Mat("RustyMetal", Color.gray));
        Prim(PrimitiveType.Cube, "Housing", t, new Vector3(0, 0.85f, 0), new Vector3(1.1f, 0.8f, 1.5f), green, true);
        Prim(PrimitiveType.Cylinder, "Exhaust", t, new Vector3(-0.35f, 1.4f, -0.5f), new Vector3(0.08f, 0.2f, 0.08f), HandleMat());
        Prim(PrimitiveType.Cylinder, "CableReel", t, new Vector3(-0.6f, 0.85f, -0.4f), new Vector3(0.5f, 0.06f, 0.5f), HandleMat(), false, new Vector3(0, 0, 90));
        foreach (var side in new[] { -1f, 1f })
            Text("GPU", t, new Vector3(0.57f * side, 0.95f, 0.2f), new Vector3(0, -90f * side, 0), 0.25f, new Color(0.95f, 0.9f, 0.6f));
        Prim(PrimitiveType.Cube, "Tongue", t, new Vector3(0, 0.4f, 1.15f), new Vector3(0.08f, 0.08f, 0.8f), HandleMat());
        Wheels(t, 0.5f, 0.55f, 0.2f);

        var sw = Prim(PrimitiveType.Cube, "PowerSwitch", t, new Vector3(0.58f, 0.95f, -0.45f), new Vector3(0.08f, 0.25f, 0.25f), Flat("SwitchPanel", new Color(0.2f, 0.2f, 0.2f)), true);
        var gpu = sw.AddComponent<GroundPowerUnit>();
        gpu.statusLight = Prim(PrimitiveType.Cube, "StatusLight", t, new Vector3(0.3f, 1.3f, -0.6f), Vector3.one * 0.12f, Flat("StatusLight", Color.red)).GetComponent<Renderer>();
        gpu.cableOrigin = Group("CableOrigin", t, new Vector3(-0.65f, 0.85f, -0.4f));

        var cart = root.AddComponent<BaggageCart>();
        cart.displayName = "Ground Power Unit";
        cart.kind = EquipmentKind.Gpu;
        cart.tongueTip = Group("TongueTip", t, new Vector3(0, 0.45f, 1.55f));
        return SavePrefab(root, "Assets/Prefabs/Equipment/GPU.prefab");
    }

    static GameObject BuildGpuCable() => Prop("GpuCable", "GPU Cable", CarryWeight.Light, 3f, t =>
    {
        Prim(PrimitiveType.Cube, "Plug", t, Vector3.zero, new Vector3(0.18f, 0.14f, 0.26f), Flat("PlugYellow", new Color(0.95f, 0.8f, 0.15f)));
        Prim(PrimitiveType.Cylinder, "Strain", t, new Vector3(0, 0, -0.18f), new Vector3(0.08f, 0.06f, 0.08f), HandleMat(), false, new Vector3(90, 0, 0));
    }, go =>
    {
        go.GetComponent<CarryableObject>().kind = CarryKind.GpuCable;
        var line = go.AddComponent<LineRenderer>();
        line.sharedMaterial = HandleMat();
        line.widthMultiplier = 0.06f;
        line.useWorldSpace = true;
        line.shadowCastingMode = ShadowCastingMode.Off;
        go.AddComponent<GpuCable>();
    });

    // Low and long so it fits under the nose; the tow point sticks out the front, the open cab is at the back.
    static GameObject BuildTug()
    {
        var (root, _) = PhysicsRoot("PushbackTug", 6000f);
        var t = root.transform;
        var paint = Mat("TugBlue", new Color(0.2f, 0.28f, 0.45f), 0.2f, 0.12f);
        var stripe = Flat("TugStripe", new Color(0.95f, 0.8f, 0.15f));
        SlideBody("Body", t, new Vector3(0, 0.4f, 0), new Vector3(1.9f, 0.7f, 4.2f), paint);
        foreach (var side in new[] { -1f, 1f })
            Prim(PrimitiveType.Cube, "Stripe", t, new Vector3(0.96f * side, 0.5f, 0), new Vector3(0.02f, 0.1f, 4.2f), stripe);
        Prim(PrimitiveType.Cube, "Towbar", t, new Vector3(0, 0.32f, 2.3f), new Vector3(0.14f, 0.12f, 0.4f), HandleMat());
        Prim(PrimitiveType.Cube, "Seat", t, new Vector3(0, 0.85f, -1.5f), new Vector3(0.55f, 0.12f, 0.5f), Mat("SeatVinyl", Color.black));
        Prim(PrimitiveType.Cube, "Roof", t, new Vector3(0, 2.15f, -1.5f), new Vector3(1.6f, 0.08f, 1.2f), paint);
        foreach (var x in new[] { -0.75f, 0.75f })
        foreach (var z in new[] { -2.05f, -0.95f })
            Prim(PrimitiveType.Cylinder, "Post", t, new Vector3(x, 1.45f, z), new Vector3(0.06f, 0.7f, 0.06f), HandleMat());
        Wheels(t, 0.85f, 1.4f, 0.35f);

        var vc = Vehicle(root, "Pushback Tug", new Vector3(0, 0.13f, -1.5f), new Vector3(-1.5f, 0.1f, -1.5f));
        vc.maxSpeed = 4f;
        vc.maxReverse = 2.5f;
        vc.acceleration = 2f;
        vc.brakeDecel = 8f;
        vc.coastDecel = 2f;
        vc.turnRate = 35f;
        vc.grip = 16f;
        vc.reliability = 0.85f;
        vc.steerResponse = 0.9f; // old tug: the wheel is more of a suggestion
        vc.kind = EquipmentKind.Tug;
        root.AddComponent<PushbackTug>().towPoint = Group("TowPoint", t, new Vector3(0, 0.3f, 2.5f));
        return SavePrefab(root, "Assets/Prefabs/Equipment/PushbackTug.prefab");
    }

    // Dashed white line for the pushback route plus the push-limit bar where the towbar may come off.
    static void PaintPushbackGuide(Vector3[] route, float limitZ)
    {
        var parent = new GameObject("PushbackGuide").transform;
        var white = Mat("FadedWhite", Color.white);
        for (int i = 0; i < route.Length - 1; i++)
        {
            Vector3 a = route[i], b = route[i + 1];
            float yaw = Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;
            for (float d = 0f; d < Vector3.Distance(a, b); d += 2.5f)
                Prim(PrimitiveType.Cube, "Dash", parent, Vector3.MoveTowards(a, b, d) + Vector3.up * 0.013f, new Vector3(0.15f, 0.02f, 1.2f), white, false, new Vector3(0, yaw, 0));
        }
        Paint("PushLimit", parent, new Vector3(-3f, 0, limitZ), new Vector2(34f, 0.35f), Mat("FadedRed", Color.red), 0.014f);
        Text("PUSH LIMIT", parent, new Vector3(9f, 0.03f, limitZ - 1f), new Vector3(90, 0, 0), 0.5f, new Color(0.8f, 0.25f, 0.2f));
    }

    // ---------- equipment ----------

    static Dictionary<string, GameObject> BuildEquipmentPrefabs() => new()
    {
        ["BaggageTractor"] = BuildTractor(),
        ["BeltLoader"] = BuildBeltLoader(),
        ["BaggageCart"] = BuildCart(),
        ["GPU"] = BuildGpu(),
        ["GpuCable"] = BuildGpuCable(),
        ["PushbackTug"] = BuildTug(),
    };

    static (GameObject root, Rigidbody rb) PhysicsRoot(string name, float mass)
    {
        var root = new GameObject(name);
        var rb = root.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ; // no rollovers (yet)
        rb.maxDepenetrationVelocity = 2f; // overlaps (spawns, teleports, a capsule inside it) nudge it out instead of launching it
        root.AddComponent<NetworkObject>();
        var nt = root.AddComponent<NetworkTransform>();
        nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        return (root, rb);
    }

    static void SlideBody(string name, Transform parent, Vector3 pos, Vector3 size, Material mat) =>
        Prim(PrimitiveType.Cube, name, parent, pos, size, mat, true).GetComponent<Collider>().sharedMaterial = slide;

    static void Wheels(Transform parent, float x, float z, float radius)
    {
        foreach (var sx in new[] { -1f, 1f })
        foreach (var sz in new[] { -1f, 1f })
            Prim(PrimitiveType.Cylinder, "Wheel", parent, new Vector3(x * sx, radius, z * sz), new Vector3(radius * 2f, 0.07f, radius * 2f), HandleMat(), false, new Vector3(0, 0, 90));
    }

    static void Hitch(Transform parent, Vector3 pos)
    {
        var h = Group("RearHitch", parent, pos);
        h.gameObject.AddComponent<TrailerHitch>();
        Prim(PrimitiveType.Cube, "HitchPin", h, Vector3.zero, new Vector3(0.12f, 0.12f, 0.2f), HandleMat());
    }

    static VehicleController Vehicle(GameObject root, string display, Vector3 seat, Vector3 exit)
    {
        var vc = root.AddComponent<VehicleController>();
        vc.displayName = display;
        vc.seatAnchor = Group("SeatAnchor", root.transform, seat);
        vc.exitPoint = Group("ExitPoint", root.transform, exit);
        return vc;
    }

    // Tractor #7: faded orange box with a lawnmower soul. Seat anchor = player feet so hips land on the cushion.
    static GameObject BuildTractor()
    {
        var (root, _) = PhysicsRoot("BaggageTractor", 1200f);
        var t = root.transform;
        var paint = Mat("TractorPaint", new Color(0.85f, 0.52f, 0.12f), 0.2f, 0.12f);
        var dark = Mat("SeatVinyl", new Color(0.15f, 0.15f, 0.15f), 0.15f, 0.1f);
        SlideBody("Body", t, new Vector3(0, 0.52f, 0), new Vector3(1.4f, 0.94f, 2.6f), paint);
        Prim(PrimitiveType.Cube, "Hood", t, new Vector3(0, 1.15f, 0.8f), new Vector3(1.3f, 0.35f, 0.9f), paint);
        Prim(PrimitiveType.Cube, "Seat", t, new Vector3(0, 1.05f, -0.45f), new Vector3(0.55f, 0.12f, 0.5f), dark);
        Prim(PrimitiveType.Cube, "SeatBack", t, new Vector3(0, 1.35f, -0.72f), new Vector3(0.55f, 0.5f, 0.1f), dark);
        Prim(PrimitiveType.Cylinder, "Column", t, new Vector3(0, 1.3f, 0.1f), new Vector3(0.06f, 0.25f, 0.06f), HandleMat(), false, new Vector3(-30, 0, 0));
        Prim(PrimitiveType.Cylinder, "SteeringWheel", t, new Vector3(0, 1.52f, -0.02f), new Vector3(0.42f, 0.02f, 0.42f), HandleMat(), false, new Vector3(-60, 0, 0));
        Prim(PrimitiveType.Cylinder, "Exhaust", t, new Vector3(0.55f, 1.55f, 0.6f), new Vector3(0.08f, 0.3f, 0.08f), Mat("RustyMetal", Color.gray));
        Wheels(t, 0.75f, 0.85f, 0.35f);
        Hitch(t, new Vector3(0, 0.45f, -1.45f));
        var tractor = Vehicle(root, "Baggage Tractor #7", new Vector3(0, 0.27f, -0.45f), new Vector3(-1.3f, 0.1f, -0.4f));
        tractor.reliability = 0.55f; // hard to start, stalls now and then, sounds awful
        tractor.steerResponse = 5f;
        tractor.kind = EquipmentKind.Tractor;
        return SavePrefab(root, "Assets/Prefabs/Equipment/BaggageTractor.prefab");
    }

    // Belt runs from low at the back to 1.5 m at the front (local +Z), overhanging so it reaches into the hold door.
    static GameObject BuildBeltLoader()
    {
        var (root, _) = PhysicsRoot("BeltLoader", 1500f);
        var t = root.transform;
        var paint = Mat("LoaderPaint", new Color(0.85f, 0.75f, 0.22f), 0.2f, 0.12f);
        var rubber = Mat("BeltRubber", new Color(0.16f, 0.16f, 0.17f), 0.5f, 0.25f);
        rubber.mainTextureScale = new Vector2(1f, 6f);
        EditorUtility.SetDirty(rubber);
        SlideBody("Chassis", t, new Vector3(0, 0.4f, 0), new Vector3(1.8f, 0.7f, 4.6f), paint);
        Prim(PrimitiveType.Cube, "Seat", t, new Vector3(-0.6f, 0.85f, -1.3f), new Vector3(0.5f, 0.12f, 0.5f), Mat("SeatVinyl", Color.black));
        Prim(PrimitiveType.Cube, "Bumper", t, new Vector3(0.25f, 1.42f, 2.95f), new Vector3(0.9f, 0.12f, 0.1f), HandleMat());
        foreach (var z in new[] { -0.8f, 1.6f })
            Prim(PrimitiveType.Cylinder, "Strut", t, new Vector3(0.25f, 0.9f, z), new Vector3(0.1f, 0.2f, 0.1f), Mat("RustyMetal", Color.gray));
        Wheels(t, 0.85f, 1.6f, 0.3f);

        var belt = Group("Belt", t, new Vector3(0.25f, 1.17f, 0.25f));
        belt.localRotation = Quaternion.Euler(-7f, 0f, 0f);
        belt.gameObject.AddComponent<BoxCollider>().size = new Vector3(0.8f, 0.12f, 5.4f);
        var surface = Prim(PrimitiveType.Cube, "Surface", belt, Vector3.zero, new Vector3(0.8f, 0.12f, 5.4f), rubber);
        foreach (var x in new[] { -0.43f, 0.43f })
            Prim(PrimitiveType.Cube, "Rail", belt, new Vector3(x, 0.1f, 0), new Vector3(0.06f, 0.2f, 5.4f), paint, true);
        var zone = new GameObject("CarryZone");
        zone.transform.SetParent(belt, false);
        var zoneBox = zone.AddComponent<BoxCollider>();
        zoneBox.isTrigger = true;
        zoneBox.center = new Vector3(0, 0.3f, 0);
        zoneBox.size = new Vector3(0.8f, 0.5f, 5.4f);
        var loader = belt.gameObject.AddComponent<BeltLoader>();
        loader.carryZone = zoneBox;
        loader.beltRenderer = surface.GetComponent<Renderer>();

        var vc = Vehicle(root, "Belt Loader", new Vector3(-0.6f, 0.13f, -1.3f), new Vector3(-1.5f, 0.1f, -1.3f));
        vc.maxSpeed = 3.5f;
        vc.maxReverse = 2f;
        vc.acceleration = 2.5f;
        vc.brakeDecel = 5f;
        vc.turnRate = 40f;
        vc.reliability = 0.8f;
        vc.steerResponse = 3f;
        vc.kind = EquipmentKind.BeltLoader;
        return SavePrefab(root, "Assets/Prefabs/Equipment/BeltLoader.prefab");
    }

    static GameObject BuildCart()
    {
        var (root, _) = PhysicsRoot("BaggageCart", 250f);
        var t = root.transform;
        var grey = Mat("CartGrey", new Color(0.48f, 0.5f, 0.48f), 0.2f, 0.15f);
        SlideBody("Chassis", t, new Vector3(0, 0.26f, 0), new Vector3(1.3f, 0.48f, 2.2f), Mat("RustyMetal", Color.gray));
        Prim(PrimitiveType.Cube, "Bed", t, new Vector3(0, 0.55f, 0), new Vector3(1.4f, 0.1f, 2.4f), grey, true);
        foreach (var x in new[] { -0.68f, 0.68f })
            Prim(PrimitiveType.Cube, "SideRail", t, new Vector3(x, 0.78f, 0), new Vector3(0.06f, 0.35f, 2.4f), grey, true);
        Prim(PrimitiveType.Cube, "FrontPanel", t, new Vector3(0, 1.1f, 1.2f), new Vector3(1.4f, 1.0f, 0.06f), grey, true);
        Prim(PrimitiveType.Cube, "BackRail", t, new Vector3(0, 0.72f, -1.2f), new Vector3(1.4f, 0.25f, 0.06f), grey, true);
        Prim(PrimitiveType.Cube, "Tongue", t, new Vector3(0, 0.45f, 1.65f), new Vector3(0.08f, 0.08f, 0.9f), HandleMat());
        Wheels(t, 0.6f, 0.8f, 0.18f);
        Hitch(t, new Vector3(0, 0.45f, -1.35f));
        var cart = root.AddComponent<BaggageCart>();
        cart.tongueTip = Group("TongueTip", t, new Vector3(0, 0.45f, 2.1f));
        cart.rearHitch = t.Find("RearHitch").GetComponent<TrailerHitch>();
        return SavePrefab(root, "Assets/Prefabs/Equipment/BaggageCart.prefab");
    }

    static void PlaceEquipment(Dictionary<string, GameObject> equipment)
    {
        var parent = new GameObject("Equipment").transform;
        void Place(string id, float x, float z = 17f, float yaw = 0f)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(equipment[id]);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(new Vector3(x, 0.1f, z), Quaternion.Euler(0, yaw, 0));
        }
        Place("BaggageTractor", -46f);
        Place("BeltLoader", -42f);
        Place("BaggageCart", -38f);
        Place("BaggageCart", -34f);
        Place("BaggageCart", -30f);
        Place("GPU", -26f);
        Place("GpuCable", -26f, 15.4f);
        Place("PushbackTug", -2f, 11f, 90f); // waiting by the stand
    }

    // Outbound bags spawn on the left of the bag room; inbound bags count as unloaded once inside the right-hand zone.
    static GameObject BuildBaggageManagerPrefab(Dictionary<string, GameObject> props)
    {
        var go = new GameObject("BaggageManager");
        go.AddComponent<NetworkObject>();
        var bm = go.AddComponent<BaggageManager>();
        bm.bagPrefabs = new[]
        {
            props["Suitcase"], props["Suitcase"], props["Suitcase"], props["TinySuitcase"],
            props["LargeSuitcase"], props["Duffel"], props["Duffel"], props["CardboardBox"],
        };
        bm.oversizedPrefab = props["HugeSuitcase"];
        // Missing bags turn up somewhere annoying: behind the shed, out by B5, on the service road, under the wing.
        bm.lostBagSpots = new[] { new Vector3(-50f, 0.5f, 31f), new Vector3(30f, 0.5f, 38f), new Vector3(-18f, 0.5f, 5f), new Vector3(4f, 0.5f, 27f) };

        var zone = new GameObject("InboundZone");
        zone.transform.SetParent(go.transform, false);
        zone.transform.position = new Vector3(-26f, 1f, -1.5f);
        var box = zone.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(7f, 2f, 7f);
        bm.inboundZone = box;

        var ink = new Color(0.9f, 0.9f, 0.85f);
        Paint("InboundFloor", go.transform, new Vector3(-26f, 0, -1.5f), new Vector2(6.8f, 6.8f), Mat("InboundGreen", new Color(0.3f, 0.5f, 0.3f), 0.15f, 0.1f, 4f), 0.02f);
        Text("INBOUND\nDROP HERE", go.transform, new Vector3(-26f, 0.04f, 0.8f), new Vector3(90, 0, 0), 0.4f, ink);
        Paint("OutboundFloor", go.transform, new Vector3(-34f, 0, -1.7f), new Vector2(7f, 5f), Mat("OutboundBlue", new Color(0.3f, 0.35f, 0.55f), 0.15f, 0.1f, 4f), 0.02f);
        Text("OUTBOUND", go.transform, new Vector3(-34f, 0.04f, 1.2f), new Vector3(90, 0, 0), 0.4f, ink);
        return SavePrefab(go, "Assets/Prefabs/BaggageManager.prefab");
    }

    static void PlaceBaggageManager(GameObject prefab, AircraftController aircraft)
    {
        var bm = ((GameObject)PrefabUtility.InstantiatePrefab(prefab)).GetComponent<BaggageManager>();
        bm.aircraft = aircraft;
        PrefabUtility.RecordPrefabInstancePropertyModifications(bm);
    }

    // ---------- aircraft ----------

    static AircraftDefinition CreateAircraftDefinition()
    {
        const string path = "Assets/ScriptableObjects/RJ200.asset";
        AssetDatabase.DeleteAsset(path);
        var def = ScriptableObject.CreateInstance<AircraftDefinition>();
        AssetDatabase.CreateAsset(def, path);
        return def;
    }

    static GameObject ColliderBox(string name, Transform parent, Vector3 center, Vector3 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var box = go.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
        return go;
    }

    // Open-ended tube along Z with the segments between gapFrom..gapTo degrees left out (0 deg = +X, 90 = up).
    // Flat-shaded quads, which is the look we want anyway.
    static Mesh TubeWithGap(float radius, float length, int segments, float gapFrom, float gapTo)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float step = 360f / segments, half = length / 2f;
        Vector3 P(float deg, float z) => new(Mathf.Cos(deg * Mathf.Deg2Rad) * radius, Mathf.Sin(deg * Mathf.Deg2Rad) * radius, z);
        for (int i = 0; i < segments; i++)
        {
            float a0 = -180f + i * step, a1 = a0 + step, mid = a0 + step / 2f;
            if (mid > gapFrom && mid < gapTo) continue;
            int b = verts.Count;
            verts.AddRange(new[] { P(a0, -half), P(a1, -half), P(a1, half), P(a0, half) });
            uvs.AddRange(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 }); // clockwise seen from outside
        }
        var mesh = new Mesh { name = "RJ200_HoldSection" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // AeroJet RJ-200: generic regional jet, ~26 m long, 20 m span. Root = nose gear on the ground, nose toward +Z.
    // Fuselage centre y=2.3, radius 1.4. Forward hold is z -6.5..-3, opening on the right (+X) side.
    static GameObject BuildAircraftPrefab(AircraftDefinition def, FlightDefinition flight)
    {
        var white = Mat("JetWhite", new Color(0.86f, 0.87f, 0.89f), 0.06f, 0.02f);
        var panel = Mat("JetPanel", new Color(0.74f, 0.75f, 0.78f), 0.06f, 0.03f);
        var livery = Flat("JetTail", new Color(0.15f, 0.3f, 0.6f));
        var glass = Flat("CockpitGlass", new Color(0.08f, 0.1f, 0.14f));
        var lining = Mat("HoldLining", new Color(0.3f, 0.3f, 0.32f), 0.15f, 0.08f);
        var grey = Mat("GearGrey", new Color(0.42f, 0.42f, 0.44f));
        var rubber = HandleMat();
        var hose = Flat("Hose", new Color(0.1f, 0.1f, 0.1f));
        var portMat = Mat("ServicePanel", new Color(0.55f, 0.56f, 0.58f), 0.1f, 0.05f);

        var root = new GameObject("RJ200");
        root.AddComponent<Rigidbody>().isKinematic = true; // moving colliders want a kinematic body
        root.AddComponent<NetworkObject>();
        var nt = root.AddComponent<NetworkTransform>();
        nt.SyncRotAngleX = nt.SyncRotAngleZ = false;
        nt.SyncScaleX = nt.SyncScaleY = nt.SyncScaleZ = false;
        var controller = root.AddComponent<AircraftController>();
        controller.definition = def;
        controller.flight = flight;
        var t = root.transform;

        // Fuselage: forward barrel, hold section with a gap, aft barrel. Cylinder caps double as hold end walls.
        Prim(PrimitiveType.Cylinder, "FuselageFwd", t, new Vector3(0, 2.3f, -0.5f), new Vector3(2.8f, 2.5f, 2.8f), white, false, new Vector3(90, 0, 0));
        Prim(PrimitiveType.Cylinder, "FuselageAft", t, new Vector3(0, 2.3f, -13.25f), new Vector3(2.8f, 6.75f, 2.8f), white, false, new Vector3(90, 0, 0));
        const string meshPath = "Assets/Art/RJ200_HoldSection.asset";
        AssetDatabase.DeleteAsset(meshPath);
        var holdMesh = TubeWithGap(1.4f, 3.5f, 20, -55f, 0f);
        AssetDatabase.CreateAsset(holdMesh, meshPath);
        var section = new GameObject("FuselageHoldSection");
        section.transform.SetParent(t, false);
        section.transform.localPosition = new Vector3(0, 2.3f, -4.75f);
        section.AddComponent<MeshFilter>().sharedMesh = holdMesh;
        section.AddComponent<MeshRenderer>().sharedMaterial = white;

        Prim(PrimitiveType.Sphere, "Nose", t, new Vector3(0, 2.25f, 2f), new Vector3(2.8f, 2.6f, 4.4f), white);
        Prim(PrimitiveType.Cube, "CockpitWindows", t, new Vector3(0, 2.85f, 3.2f), new Vector3(1.9f, 0.45f, 1.1f), glass, false, new Vector3(-25, 0, 0));
        Prim(PrimitiveType.Sphere, "TailCone", t, new Vector3(0, 2.5f, -20f), new Vector3(2.4f, 2.3f, 5f), white);
        foreach (var side in new[] { -1f, 1f })
        {
            Prim(PrimitiveType.Cube, "Windows", t, new Vector3(1.37f * side, 2.85f, -10f), new Vector3(0.05f, 0.26f, 15f), glass);
            Prim(PrimitiveType.Cube, "CheatLine", t, new Vector3(1.39f * side, 2.5f, -9f), new Vector3(0.04f, 0.14f, 22f), livery);
            Text("AEROJET", t, new Vector3(1.43f * side, 3.25f, -12f), new Vector3(0, -90f * side, 0), 0.45f, new Color(0.15f, 0.3f, 0.6f));

            Prim(PrimitiveType.Cube, "Wing", t, new Vector3(5.6f * side, 2.2f, -10.5f), new Vector3(9f, 0.28f, 3.2f), white, true, new Vector3(0, 10f * side, 0));
            Prim(PrimitiveType.Cylinder, "Engine", t, new Vector3(4.2f * side, 1.5f, -8.8f), new Vector3(1.3f, 1.5f, 1.3f), white, true, new Vector3(90, 0, 0));
            Prim(PrimitiveType.Cylinder, "Intake", t, new Vector3(4.2f * side, 1.5f, -7.28f), new Vector3(1.1f, 0.02f, 1.1f), glass, false, new Vector3(90, 0, 0));
            Prim(PrimitiveType.Cube, "Pylon", t, new Vector3(4.2f * side, 2.05f, -9.2f), new Vector3(0.2f, 0.5f, 1.6f), white);

            Prim(PrimitiveType.Cylinder, "MainStrut", t, new Vector3(2.2f * side, 0.9f, -10.5f), new Vector3(0.2f, 0.55f, 0.2f), grey);
            foreach (var dx in new[] { -0.22f, 0.22f })
                Prim(PrimitiveType.Cylinder, "MainWheel", t, new Vector3(2.2f * side + dx, 0.4f, -10.5f), new Vector3(0.8f, 0.1f, 0.8f), rubber, true, new Vector3(0, 0, 90));
        }
        Prim(PrimitiveType.Cube, "Fin", t, new Vector3(0, 5.2f, -20.5f), new Vector3(0.3f, 4.2f, 3.4f), livery, true, new Vector3(-20, 0, 0));
        Prim(PrimitiveType.Cube, "Stabilizer", t, new Vector3(0, 7.15f, -22f), new Vector3(8f, 0.22f, 2.2f), white); // T-tail

        // Nose gear sits on the root; this is also where the tug will hook up.
        Prim(PrimitiveType.Cylinder, "NoseStrut", t, new Vector3(0, 0.75f, 0), new Vector3(0.15f, 0.5f, 0.15f), grey);
        foreach (var dx in new[] { -0.16f, 0.16f })
            Prim(PrimitiveType.Cylinder, "NoseWheel", t, new Vector3(dx, 0.3f, 0), new Vector3(0.6f, 0.08f, 0.6f), rubber, false, new Vector3(0, 0, 90)); // no collider: the tug lives here
        Group("PushbackPoint", t, new Vector3(0, 0.3f, 0.6f));

        // Collision shell: boxes around everything except the hold cavity.
        ColliderBox("Col_Upper", t, new Vector3(0, 3.05f, -8.5f), new Vector3(2.8f, 1.5f, 25f));
        ColliderBox("Col_BellyFwd", t, new Vector3(0, 1.6f, 0.25f), new Vector3(2.8f, 1.4f, 6.5f));
        ColliderBox("Col_BellyAft", t, new Vector3(0, 1.6f, -13.25f), new Vector3(2.8f, 1.4f, 13.5f));
        ColliderBox("Col_HoldBelly", t, new Vector3(0, 1.0f, -4.75f), new Vector3(2.0f, 0.2f, 3.5f));
        ColliderBox("Col_HoldLeft", t, new Vector3(-1.2f, 1.6f, -4.75f), new Vector3(0.4f, 1.4f, 3.5f));

        // Forward hold: lined box inside the gap, floor at y 1.3 (chest height, good for throwing).
        Prim(PrimitiveType.Cube, "HoldFloor", t, new Vector3(0, 1.2f, -4.75f), new Vector3(1.96f, 0.2f, 3.5f), lining, true);
        Prim(PrimitiveType.Cube, "HoldWall", t, new Vector3(-0.98f, 1.8f, -4.75f), new Vector3(0.1f, 1.0f, 3.5f), lining, true);
        Prim(PrimitiveType.Cube, "HoldCeiling", t, new Vector3(0, 2.33f, -4.75f), new Vector3(1.96f, 0.06f, 3.5f), lining, true);
        var holdGo = new GameObject("ForwardHold");
        holdGo.transform.SetParent(t, false);
        holdGo.transform.localPosition = new Vector3(0, 1.8f, -4.75f);
        holdGo.AddComponent<BoxCollider>().size = new Vector3(1.9f, 0.95f, 3.4f);
        holdGo.AddComponent<CargoHold>().holdName = "Forward Hold";

        // Cargo door: hinge along the top of the opening, panel hangs along the hull when closed and swings up/out.
        var cargoDoor = Door("CargoDoor", t, new Vector3(1.42f, 2.3f, -4.75f), "Forward Cargo Door", new Vector3(0, 0, -27f), new Vector3(0, 0, 100f));
        Prim(PrimitiveType.Cube, "Panel", cargoDoor, new Vector3(0, -0.635f, 0), new Vector3(0.06f, 1.27f, 3.5f), panel, true);
        Prim(PrimitiveType.Cube, "Handle", cargoDoor, new Vector3(0.04f, -1.1f, 0), new Vector3(0.04f, 0.08f, 0.5f), rubber);

        // Passenger door: forward left, swings forward on a vertical hinge. Doorway is dark behind it.
        Prim(PrimitiveType.Cube, "Doorway", t, new Vector3(-1.4f, 3.15f, 0.55f), new Vector3(0.03f, 1.7f, 0.9f), glass);
        var paxDoor = Door("PassengerDoor", t, new Vector3(-1.44f, 2.3f, 1.0f), "Passenger Door", Vector3.zero, new Vector3(0, -100f, 0));
        paxDoor.GetComponent<AircraftDoor>().isCargoDoor = false;
        Prim(PrimitiveType.Cube, "Panel", paxDoor, new Vector3(0, 0.85f, -0.45f), new Vector3(0.06f, 1.7f, 0.9f), panel, true);

        Service(t, "GPUPort", ServiceType.GroundPower, "Ground Power", "GPU Cable", 0f, false, new Vector3(0.75f, 1.05f, 1.5f), 1f);
        var gpuPort = t.Find("GPUPort").GetComponent<AircraftServicePoint>();
        gpuPort.requiresGpuCable = true;
        gpuPort.connectedVisual.transform.localScale = Vector3.zero; // the real cable shows instead of the fake hose

        // Where chocks and cones belong while parked (aircraft-local, so they follow wherever it stopped).
        Zone("ChockZone", t, CarryKind.Chock, 2, new Vector3(0, 0.25f, 0), new Vector3(1.6f, 0.6f, 2.2f), Flat("ChockMark", new Color(0.8f, 0.7f, 0.1f)));
        var coneMark = Flat("ConeMark", new Color(0.9f, 0.4f, 0.1f));
        Zone("ConeZoneLeftWing", t, CarryKind.Cone, 1, new Vector3(-10.8f, 0.3f, -11.5f), new Vector3(2f, 0.8f, 2f), coneMark);
        Zone("ConeZoneRightWing", t, CarryKind.Cone, 1, new Vector3(10.8f, 0.3f, -11.5f), new Vector3(2f, 0.8f, 2f), coneMark);
        Zone("ConeZoneTail", t, CarryKind.Cone, 1, new Vector3(0, 0.3f, -25.5f), new Vector3(2f, 0.8f, 2f), coneMark);
        Service(t, "FuelPanel", ServiceType.Fuel, "Fuel", "Fuel Hose", 25f, true, new Vector3(5.2f, 1.9f, -10.8f), 1f);
        Service(t, "WaterPanel", ServiceType.PotableWater, "Potable Water", "Water Hose", 15f, true, new Vector3(-0.75f, 1.05f, -16f), -1f);
        Service(t, "LavPanel", ServiceType.Lavatory, "Lavatory", "Lav Hose", 15f, true, new Vector3(0.75f, 1.05f, -17.5f), 1f);

        return SavePrefab(root, "Assets/Prefabs/Aircraft/RJ200.prefab");

        void Zone(string name, Transform parent, CarryKind kind, int required, Vector3 center, Vector3 size, Material mark)
        {
            var go = Group(name, parent, center);
            go.gameObject.AddComponent<BoxCollider>().size = size;
            var zone = go.gameObject.AddComponent<AircraftZone>();
            zone.kind = kind;
            zone.required = required;
            zone.marker = Prim(PrimitiveType.Cube, "Marker", go, new Vector3(0, 0.015f - center.y, 0), new Vector3(size.x * 0.7f, 0.02f, size.z * 0.7f), mark).GetComponent<Renderer>();
        }

        Transform Door(string name, Transform parent, Vector3 hinge, string label, Vector3 closed, Vector3 open)
        {
            var d = Group(name, parent, hinge);
            d.localRotation = Quaternion.Euler(closed);
            var door = d.gameObject.AddComponent<AircraftDoor>();
            door.doorName = label;
            door.closedEuler = closed;
            door.openEuler = open;
            return d;
        }

        void Service(Transform parent, string name, ServiceType type, string display, string hoseName, float duration, bool required, Vector3 pos, float side)
        {
            var box = Prim(PrimitiveType.Cube, name, parent, pos, Vector3.one * 0.32f, portMat, true);
            var light = Prim(PrimitiveType.Cube, "StatusLight", box.transform, new Vector3(0.5f * side, 0.3f, 0), Vector3.one * 0.3f, Flat("StatusLight", Color.red));
            var connectedHose = Prim(PrimitiveType.Cylinder, "Hose", box.transform, new Vector3(0.3f * side, -1.6f, 0), new Vector3(0.3f, 1.6f, 0.3f), hose, false, new Vector3(0, 0, 12f * side));
            connectedHose.SetActive(false);
            Text(display.ToUpper(), box.transform, new Vector3(0.52f * side, -0.25f, 0), new Vector3(0, -90f * side, 0), 0.3f, Color.white);
            var sp = box.AddComponent<AircraftServicePoint>();
            sp.type = type;
            sp.displayName = display;
            sp.hoseName = hoseName;
            sp.duration = duration;
            sp.requiredForDeparture = required;
            sp.statusLight = light.GetComponent<Renderer>();
            sp.connectedVisual = connectedHose;
        }
    }

    // Gate B4: arrive along the taxiway, turn onto the lead-in line, stop with the nose gear on the stop bar.
    // Pushback swings the tail west so the nose ends up pointing east down the taxiway.
    static AircraftController PlaceAircraft(GameObject prefab)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        var ac = go.GetComponent<AircraftController>();
        ac.arrivalPath = new[] { new Vector3(-70, 0, 85), new Vector3(0, 0, 85), new Vector3(12, 0, 72), new Vector3(12, 0, 50), new Vector3(12, 0, 15) };
        ac.departurePath = new[] { new Vector3(40, 0, 85), new Vector3(100, 0, 85) };
        go.transform.SetPositionAndRotation(ac.arrivalPath[0], Quaternion.Euler(0, 90, 0));
        PrefabUtility.RecordPrefabInstancePropertyModifications(ac);
        PaintPushbackGuide(new[] { new Vector3(12, 0, 15), new Vector3(12, 0, 58), new Vector3(2, 0, 80), new Vector3(-10, 0, 85) }, ac.pushLimitZ);
        return ac;
    }

    // Not the RJ-200, just background. No colliders except the fuselage.
    static void DistantJet(Transform world, Vector3 pos, float yaw)
    {
        var body = Mat("JetWhite", new Color(0.85f, 0.86f, 0.88f), 0.06f, 0.02f);
        var tail = Flat("JetTail", new Color(0.15f, 0.3f, 0.6f));
        var j = Group("DistantJet", world, pos, yaw);
        Prim(PrimitiveType.Cylinder, "Fuselage", j, new Vector3(0, 2.6f, 0), new Vector3(3f, 14f, 3f), body, true, new Vector3(90, 0, 0));
        Prim(PrimitiveType.Sphere, "Nose", j, new Vector3(0, 2.6f, 14f), new Vector3(3f, 3f, 3f), body);
        Prim(PrimitiveType.Cube, "Wings", j, new Vector3(0, 2f, 1f), new Vector3(28f, 0.3f, 4f), body);
        Prim(PrimitiveType.Cube, "Tailfin", j, new Vector3(0, 6f, -12.5f), new Vector3(0.3f, 5f, 3f), tail);
        Prim(PrimitiveType.Cube, "Stabilizer", j, new Vector3(0, 3.2f, -13f), new Vector3(9f, 0.25f, 2f), body);
        foreach (var x in new[] { -1.6f, 1.6f }) Prim(PrimitiveType.Cylinder, "Gear", j, new Vector3(x, 0.5f, 0), new Vector3(1f, 0.25f, 1f), HandleMat(), false, new Vector3(0, 0, 90));
        Prim(PrimitiveType.Cylinder, "NoseGear", j, new Vector3(0, 0.4f, 11f), new Vector3(0.8f, 0.2f, 0.8f), HandleMat(), false, new Vector3(0, 0, 90));
    }
}
