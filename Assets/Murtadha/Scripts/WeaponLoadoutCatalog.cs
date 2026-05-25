using UnityEngine;

public readonly struct WeaponLoadout
{
    public readonly string[] ResourcePaths;
    public readonly float TargetSize;
    public readonly Vector3 PlayerLocalPosition;
    public readonly Vector3 PlayerLocalEuler;
    public readonly Vector3 EnemyLocalPosition;
    public readonly Vector3 EnemyLocalEuler;

    public WeaponLoadout(
        string[] resourcePaths,
        float targetSize,
        Vector3 playerLocalPosition,
        Vector3 playerLocalEuler,
        Vector3 enemyLocalPosition,
        Vector3 enemyLocalEuler)
    {
        ResourcePaths = resourcePaths;
        TargetSize = targetSize;
        PlayerLocalPosition = playerLocalPosition;
        PlayerLocalEuler = playerLocalEuler;
        EnemyLocalPosition = enemyLocalPosition;
        EnemyLocalEuler = enemyLocalEuler;
    }

    public GameObject LoadPrefab()
    {
        if (ResourcePaths == null)
            return null;

        for (int i = 0; i < ResourcePaths.Length; i++)
        {
            string path = ResourcePaths[i];
            if (string.IsNullOrWhiteSpace(path))
                continue;

            GameObject prefab = Resources.Load<GameObject>(path);
            if (prefab != null)
                return prefab;
        }

        return null;
    }
}

public static class WeaponLoadoutCatalog
{
    private static readonly Vector3 OneHandedGripEuler = new Vector3(0f, 0f, 90f);
    private static readonly Vector3 ReversedOneHandedGripEuler = new Vector3(0f, 180f, 90f);
    private static readonly Vector3 DefaultPlayerLocalPosition = new Vector3(-0.015f, -0.005f, 0f);
    private static readonly Vector3 DefaultPlayerLocalEuler = OneHandedGripEuler;
    private static readonly Vector3 DefaultEnemyLocalPosition = new Vector3(-0.01f, -0.0025f, 0f);
    private static readonly Vector3 DefaultEnemyLocalEuler = OneHandedGripEuler;
    // Level 1 knife keeps its grip pivot very close to the blade center on the
    // imported FBX, so the generic Crosby one-handed preset tucks the mesh into
    // the palm. The original enemy-only knife basis uses the real hand bone and
    // a forward Y offset / X-axis quarter-turn so the blade clears the wrist
    // from the first frame.
    private static readonly Vector3 Level1EnemyLocalPosition = new Vector3(0f, 0.05f, 0f);
    private static readonly Vector3 Level1EnemyLocalEuler = new Vector3(-90f, 0f, 0f);
    // Level 5 nunchucks pivot around the chain midpoint, not around a single
    // handle grip. They still need the enemy-only visibility lift on Crosby,
    // but the actual weapon-facing basis should stay in the same one-handed
    // family as the player and then apply the usual Crosby end-for-end flip so
    // the visible handle points forward instead of backward.
    private static readonly Vector3 Level5EnemyLocalPosition = new Vector3(0f, 0.05f, 0f);
    private static readonly Vector3 Level5EnemyLocalEuler = ReversedOneHandedGripEuler;
    private static readonly Vector3 MediumPlayerLocalPosition = new Vector3(-0.03f, -0.005f, 0f);
    private static readonly Vector3 MediumEnemyLocalPosition = new Vector3(-0.025f, -0.0025f, 0f);
    private static readonly Vector3 LongPlayerLocalPosition = new Vector3(-0.045f, -0.005f, 0f);
    private static readonly Vector3 LongEnemyLocalPosition = new Vector3(-0.035f, -0.0025f, 0f);
    // Katana player grip: catalog fallback (PlayerController overrides with
    // PlayerKatanaGripLocalEuler directly, so these are secondary).
    private static readonly Vector3 KatanaPlayerLocalEuler    = new Vector3(0f, 0f, 160f);
    private static readonly Vector3 KatanaPlayerLocalPosition = new Vector3(-0.045f, -0.005f, 0f);
    // Enemy katana grip matches player grip exactly — socket stabilization in
    // AttachWeaponToHand already cancels the hand bone's local rotation, so no
    // Y=180 Crosby correction is needed here.
    private static readonly Vector3 KatanaEnemyLocalEuler = new Vector3(0f, 0f, 160f);
    // Same Crosby hand basis issue as the katana: the bat needs an enemy-only
    // end-for-end correction while preserving the player grip basis.
    private static readonly Vector3 BaseballBatEnemyLocalEuler = ReversedOneHandedGripEuler;
    // Level 15 shares the same enemy hand-basis inversion, so it also needs
    // an enemy-only end-for-end correction to match the player's forward grip.
    private static readonly Vector3 Level15EnemyLocalEuler = ReversedOneHandedGripEuler;
    // Level 16 shield also inherits the enemy hand-basis inversion, so it
    // needs an enemy-only forward-facing correction to match the player grip.
    private static readonly Vector3 Level16EnemyLocalEuler = ReversedOneHandedGripEuler;
    private static readonly Vector3 PolePlayerLocalPosition = new Vector3(-0.06f, -0.0075f, 0f);
    private static readonly Vector3 PoleEnemyLocalPosition = new Vector3(-0.05f, -0.003f, 0f);
    // Level 3 shovel: tilt blade forward/up so it clears the ground during idle.
    // -60° X pitch lifts the blade end; Z=90 keeps the standard one-handed convention.
    private static readonly Vector3 ShovelPlayerLocalPosition = new Vector3(-0.06f, -0.0075f, 0f);
    private static readonly Vector3 ShovelPlayerLocalEuler    = new Vector3(-90f, 0f, 90f);
    private static readonly Vector3 ShovelEnemyLocalPosition  = new Vector3(-0.05f, -0.003f, 0f);
    private static readonly Vector3 ShovelEnemyLocalEuler     = new Vector3(-90f, 0f, 90f);
    // Level 6 wrench: dedicated exact grip values. The imported FBX long axis
    // is local Y with bounds roughly [-25.1559 .. +18.9399]. After the 0.40m
    // autoscale, the handle butt sits ~0.228m from the pivot on the -Y end.
    // With the project-wide Z=90 one-handed convention that butt end maps to
    // +X in socket space, so the weapon root must move in -X for the palm to
    // close around the start of the handle instead of the middle/head.
    private static readonly Vector3 WrenchPlayerLocalPosition = new Vector3(-0.208f, -0.0125f, 0.0025f);
    private static readonly Vector3 WrenchPlayerLocalEuler = new Vector3(10f, 0f, 90f);
    private static readonly Vector3 WrenchEnemyLocalPosition = new Vector3(-0.182f, -0.0085f, 0.0025f);
    // Level 6 uses the same believable player-local wrench pose once the enemy
    // hand basis is normalized on the socket side. Without that normalization,
    // Crosby's hand drives the tool forward/backward regardless of root grip.
    private static readonly Vector3 WrenchEnemyLocalEuler = WrenchPlayerLocalEuler;
    // Level 7 crowbar: FBX long axis is local X (after the baked 270° X root rotation).
    // Handle/straight end at -X (~-0.282m at 0.55m autoscale); hook at +X (~+0.267m).
    //
    // PLAYER (Ronin / tag_accessory_right): Ry(180) flips the crowbar end-for-end so
    // hook → socket -X (Ronin forward/striking direction). Position -0.220 places the
    // hand at mid-handle, ~0.063m from the butt. Prior values (pos=-0.135, Z=104)
    // left the grip 0.29m from the socket (visually floating).
    //
    // ENEMY (Crosby / bip_hand_R): The Crosby hand basis is end-for-end inverted
    // relative to Ronin — the same pattern seen on katana, bat, shield, and level-15
    // weapons (all need a Y=180 enemy correction vs player). With identity rotation
    // the crowbar +X (hook) maps directly to Crosby socket +X (forward/striking
    // direction), and the grip offset flips sign: +0.219 in X places the hand at
    // mid-handle. No socket normalisation needed for the crowbar (unlike the wrench).
    private static readonly Vector3 CrowbarPlayerLocalPosition = new Vector3(-0.220f, -0.005f, 0.002f);
    private static readonly Vector3 CrowbarPlayerLocalEuler    = new Vector3(0f, 180f, 0f);
    private static readonly Vector3 CrowbarEnemyLocalPosition  = new Vector3(0.219f, -0.003f, 0.002f);
    private static readonly Vector3 CrowbarEnemyLocalEuler     = new Vector3(0f, 0f, 0f);
    // Level 8 hammer: FBX long axis is Z in prefab root space (SMR child baked
    // rotation (270,90,0) maps SMR local +Z → world +Z at unit scale).
    // Head bulk at Z=4.95–5.42 raw; handle butt is the root pivot at Z=0.
    // At 0.85m autoscale (≈0.157): handle=0.776m, head=0.074m.
    //
    // AXIS DERIVATION (from verified axe reference, level 9):
    //   The axe uses euler (0,180,90) → Ry(180)*Rx(0)*Rz(90).
    //   Its head (at axe −X) maps to socket −Y (the forward/striking axis),
    //   confirmed by "head extends outward away from the body" in the axe comment.
    //   For the hammer, the head is at weapon +Z. We need R*(0,0,1) = (0,−1,0):
    //     Rz(90)*(0,0,1) = (0,0,1)   [Z unchanged by any Rz]
    //     Rx(90)*(0,0,1) = (0,−1,0)  [maps +Z → −Y regardless of Ry/Rz]
    //   So euler (90,0,90) maps hammer head (+Z) → socket −Y = forward. ✓
    //
    // POSITION DERIVATION:
    //   With euler (90,0,90), weapon +Z (handle direction) maps to socket −Y.
    //   The handle runs from the butt (weapon root, Z=0) in the socket −Y direction.
    //   Grip at weapon Z_grip (scaled) lands at socket Y = Y_pos − Z_grip.
    //   For grip at socket origin: Y_pos = Z_grip.
    //   Bottom-of-handle grip (ref: Husky sledgehammer photo):
    //   Butt ~4cm behind palm → Y_pos=0.04 → grip at weapon Z=0.04 lands at palm.
    //   Head at socket Y = 0.04−0.85 = −0.81m (81cm forward). Full reach. ✓
    //
    // ENEMY: shares socket normalisation euler with wrench so after normalisation
    // the Crosby socket −Y is also the striking direction; same weapon euler applies.
    private static readonly Vector3 HammerPlayerLocalPosition = new Vector3(0.0f, 0.040f, 0.0f);
    private static readonly Vector3 HammerPlayerLocalEuler    = new Vector3(90f,  0f,   90f);
    private static readonly Vector3 HammerEnemyLocalPosition  = new Vector3(0.0f, 0.220f, 0.0f);
    private static readonly Vector3 HammerEnemyLocalEuler     = new Vector3(90f,  0f,   90f);
    // Crosby's raw hand basis needs a socket-side normalization for tool-style
    // one-handed grips so the authored player-like root pose doesn't end up
    // pointing forward/backward on the enemy.
    private static readonly Vector3 WrenchEnemySocketLocalEuler = new Vector3(15.2525f, -24.1971f, -111.6271f);
    private static readonly Vector3 HammerEnemySocketLocalEuler = new Vector3(15.2525f, -24.1971f, -111.6271f);
    // Crosby's hand basis points the pole shaft backward relative to the
    // player wrist basis, so pole levels need a pre-grip socket flip on the
    // enemy side instead of more per-weapon position nudges.
    private static readonly Vector3 PoleEnemySocketLocalEuler = new Vector3(0f, 180f, 0f);
    // Level 12 hand saw: the imported mesh is a one-handed carpentry saw, not
    // a chainsaw/hammer-style tool. Its handle sits near the +X end and the
    // blade extends toward -X. Rotate +X behind the palm so -X points forward,
    // then seat the socket on the handle bounds instead of on the model pivot.
    public static readonly Vector3 ChainsawPlayerLocalPosition = new Vector3(-0.066f, -0.39f, 0.044f);
    public static readonly Vector3 ChainsawPlayerLocalEuler = new Vector3(-177.177f, -175.886f, 88.481f);
    // Enemy saw: bip_hand_R direct (no stabilization). The player also resolves
    // to bip_hand_R as socket parent (PlayerController line 4044), so both use the
    // same bone — identical euler and position must produce the same visual grip.
    public static readonly Vector3 ChainsawEnemyLocalPosition = ChainsawPlayerLocalPosition;
    public static readonly Vector3 ChainsawEnemyLocalEuler = ChainsawPlayerLocalEuler;
    public const float ChainsawTargetSize = 0.78f;
    private static readonly Vector3 SawPlayerLocalPosition = ChainsawPlayerLocalPosition;
    private static readonly Vector3 SawPlayerLocalEuler    = ChainsawPlayerLocalEuler;
    private static readonly Vector3 SawEnemyLocalPosition  = ChainsawEnemyLocalPosition;
    private static readonly Vector3 SawEnemyLocalEuler     = ChainsawEnemyLocalEuler;
    private static readonly Vector3 SawRuntimeMeshLocalEuler = ChainsawEnemyLocalEuler;
    // Kept only so older scene objects named with these anchors do not break
    // during editor preview. New level 12 attachment uses explicit offsets.
    private const string SawRuntimeSocketAnchorName = "Level12SawSocketAnchor";
    private const string SawRuntimeHandleAnchorName = "Level12SawHandleAnchor";
    // Fallback in root-local pre-scale coords. Negative Y selects the rear
    // handle side; positive Y was selecting the saw head/blade side.
    private static readonly Vector3 SawRuntimeFallbackGripPoint = new Vector3(50f, -120f, 1.5f);
    private const float SawPlayerHandleGripXNormalized = 0.90f;
    private const float SawPlayerHandleGripYNormalized = 0.50f;
    private const float SawPlayerHandleGripZNormalized = 0.50f;
    private const float SawHandleGripXNormalized = SawPlayerHandleGripXNormalized;
    private const float SawHandleGripYNormalized = SawPlayerHandleGripYNormalized;
    private const float SawHandleGripZNormalized = 0.50f;
    // Level 13 sickle: once the player uses the real hand/wrist bone instead
    // of the accessory tag socket, the sickle matches the enemy's forward
    // one-handed basis with only a small player-side inward grip offset.
    private static readonly Vector3 SicklePlayerLocalPosition = new Vector3(-0.015f, -0.005f, 0f);
    private static readonly Vector3 SicklePlayerLocalEuler = new Vector3(0f, 0f, 90f);
    // Level 14 morgenstern: the imported pivot sits at the spiked head, so the
    // grip needs two pieces of compensation:
    // 1) move the hand to the far handle end instead of the pivot/head, and
    // 2) rotate the model based on its actual dominant shaft axis so the head
    //    points onto the player's forward strike axis instead of hanging to the side.
    private static readonly Vector3 MorgensternPlayerLocalPosition = MediumPlayerLocalPosition;
    private const float MorgensternHandleGripInsetNormalized = 0.82f;
    // ── Level 9 axe (single source of truth) ──
    // Empirically verified on the real Crosby body (bip_hand_R) with the
    // runtime 0.70m autoscale applied. The axe FBX has an extremely
    // asymmetric pivot: long axis is X with mesh range [-0.0094..+0.0431]
    // and the dense head bulk sitting at X≈-0.0094. After autoscale
    // (uniformScale = 0.70 / 0.0525 ≈ 13.33) the head is 0.125m from the
    // pivot and the handle butt is 0.575m from the pivot.
    //
    // The Z=90 component preserves the project-wide one-handed grip
    // convention; under Unity's intrinsic ZXY order Z=90 maps mesh +X to
    // socket -Y, which is why the offset lives on the socket Y axis (NOT
    // X like the wrench/crowbar/hammer). The 180° Y flip swaps the model
    // end-for-end so the wooden handle sits in the palm and the head
    // extends outward away from the body.
    //
    // Player Y = -0.475 places the hand 0.10m from the butt and 0.60m
    // from the head — the natural grip section of the handle, not at the
    // extreme butt edge. Level 9 enemy now uses the same final handle
    // placement once the socket basis is normalized to the player's style.
    private static readonly Vector3 AxePlayerLocalPosition = new Vector3(0f, -0.30f, 0.015f);
    private static readonly Vector3 AxePlayerLocalEuler = new Vector3(0f, 180f, 90f);
    private static readonly Vector3 AxeEnemyLocalPosition = AxePlayerLocalPosition;
    private static readonly Vector3 AxeEnemyLocalEuler = AxePlayerLocalEuler;

    // ── Guaranteed fallback paths (tried in order when the level weapon is missing) ──
    private static readonly string[] FallbackPaths =
    {
        "Weapons/Imported/tactical-knife(level1)/source/TacticalKnife/Tactical Knife",
        "Weapons/TacticalKnife/TacticalKnife",
        "Weapons/KnuckleDuster",
        "Weapons/BlinkDaggerPack/_PrefabsDaggers/Dagger1_3_5",
        "Weapons/BlinkDaggerPack/Meshes_Dagger/Dagger1_3",
    };

    /// <summary>
    /// Loads the weapon prefab for the given level with a guaranteed fallback chain.
    /// If the level's weapon is missing, tries: Level 1 knife → KnuckleDuster → BlinkDagger.
    /// <paramref name="targetSize"/> is set to the appropriate size for whichever weapon loaded.
    /// </summary>
    public static GameObject LoadPrefabWithFallback(int level, out float targetSize)
    {
        WeaponLoadout loadout = Get(level);
        GameObject prefab = loadout.LoadPrefab();
        if (prefab != null)
        {
            targetSize = loadout.TargetSize;
            return prefab;
        }

        Debug.LogWarning($"[WeaponLoadoutCatalog] Level {level} weapon missing — trying fallbacks.");

        for (int i = 0; i < FallbackPaths.Length; i++)
        {
            prefab = Resources.Load<GameObject>(FallbackPaths[i]);
            if (prefab != null)
            {
                Debug.LogWarning($"[WeaponLoadoutCatalog] Fallback loaded: '{FallbackPaths[i]}'");
                targetSize = 0.30f; // safe knife-sized default
                return prefab;
            }
        }

        Debug.LogWarning($"[WeaponLoadoutCatalog] ALL weapon fallbacks failed for level {level}.");
        targetSize = 0.30f;
        return null;
    }

    public static WeaponLoadout Get(int level)
    {
        switch (Mathf.Clamp(level, 1, 16))
        {
            case 1:
                return CreateExactGrip(
                    0.32f,
                    DefaultPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    Level1EnemyLocalPosition,
                    Level1EnemyLocalEuler,
                    "Weapons/Imported/tactical-knife(level1)/source/TacticalKnife/Tactical Knife",
                    "Weapons/TacticalKnife/TacticalKnife");
            case 2:
                return CreateExactGrip(
                    0.95f,
                    KatanaPlayerLocalPosition,
                    KatanaPlayerLocalEuler,
                    LongEnemyLocalPosition,
                    KatanaEnemyLocalEuler,
                    "Weapons/Imported/Katana(level2)/source/Katana_low",
                    "Weapons/Imported/Katana(level2)/source/melee");
            case 3:
                return CreateExactGrip(
                    0.72f,
                    ShovelPlayerLocalPosition,
                    ShovelPlayerLocalEuler,
                    ShovelEnemyLocalPosition,
                    ShovelEnemyLocalEuler,
                    "Weapons/Imported/shovel(level3)/source/Shovel/Shovel");
            case 4:
                return CreateExactGrip(
                    0.85f,
                    LongPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    LongEnemyLocalPosition,
                    BaseballBatEnemyLocalEuler,
                    "Weapons/Imported/baseball-bat(level4)/source/baseball_bat_1k");
            case 5:
                return CreateExactGrip(
                    0.30f,
                    DefaultPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    Level5EnemyLocalPosition,
                    Level5EnemyLocalEuler,
                    "Weapons/Imported/nunchucks(level5)/Nunchucks");
            case 6:
                return CreateExactGrip(
                    0.40f,
                    WrenchPlayerLocalPosition,
                    WrenchPlayerLocalEuler,
                    WrenchEnemyLocalPosition,
                    WrenchEnemyLocalEuler,
                    "Weapons/Imported/Wrench(level6)/source/PipeWrenchUnreal");
            case 7:
                return CreateExactGrip(
                    0.55f,
                    CrowbarPlayerLocalPosition,
                    CrowbarPlayerLocalEuler,
                    CrowbarEnemyLocalPosition,
                    CrowbarEnemyLocalEuler,
                    "Weapons/Imported/crowbar(level7)/source/CrowbarV2");
            case 8:
                return CreateExactGrip(
                    0.65f,
                    HammerPlayerLocalPosition,
                    HammerPlayerLocalEuler,
                    HammerEnemyLocalPosition,
                    HammerEnemyLocalEuler,
                    "Weapons/Imported/Hammer(level8)l/source/Sledgehammer/Sledge hammer");
            case 9:
                return CreateExactGrip(
                    0.70f,
                    AxePlayerLocalPosition,
                    AxePlayerLocalEuler,
                    AxeEnemyLocalPosition,
                    AxeEnemyLocalEuler,
                    "Weapons/Imported/axe(level9)/source/axe");
            case 10:
                return CreatePlayerMatchedGrip(
                    0.70f,
                    PolePlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    "Weapons/Imported/Spear(level10)/source/Spear/Spear");
            case 11:
                return CreatePlayerMatchedGrip(
                    1.00f,
                    LongPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    "Weapons/Imported/nailed-plank(level11)/source/NailedPlank/NailedPlank");
            case 12:
                return CreateExactGrip(
                    ChainsawTargetSize,
                    SawPlayerLocalPosition,
                    SawPlayerLocalEuler,
                    SawEnemyLocalPosition,
                    SawEnemyLocalEuler,
                    "Weapons/Imported/saw(level12)/source/extracted/saw_low");
            case 13:
                return CreateExactGrip(
                    0.72f,
                    SicklePlayerLocalPosition,
                    SicklePlayerLocalEuler,
                    SicklePlayerLocalPosition,
                    SicklePlayerLocalEuler,
                    "Weapons/Imported/sickle(level13)/source/Sickle");
            case 14:
                // Morgenstern grip is forced by hardcoded post-grip overrides in
                // PlayerController.AttachWeaponToHand and EnemyController.AttachWeaponToHand.
                // Catalog values below are placeholders only — the controller switch
                // overrides them immediately so the head ends up forward, handle in palm.
                return CreateMediumGrip(0.50f,
                    "Weapons/Imported/medieval(level14)/source/Medieval_morgenstern_low2 scene");
            case 15:
                return CreateExactGrip(
                    0.60f,
                    MediumPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    MediumEnemyLocalPosition,
                    Level15EnemyLocalEuler,
                    "Weapons/Imported/l3fte(level15)/source/L3FT_E");
            case 16:
                return CreateExactGrip(
                    0.90f,
                    DefaultPlayerLocalPosition,
                    DefaultPlayerLocalEuler,
                    DefaultEnemyLocalPosition,
                    Level16EnemyLocalEuler,
                    "Weapons/Imported/shield(level16)/source/RiotShield/Riot Shield");
            default:
                // No fallback FBX — primitive knife generated in code.
                return Create(0.30f);
        }
    }

    public static Vector3 GetEnemySocketLocalEuler(int level)
    {
        switch (Mathf.Clamp(level, 1, 16))
        {
            case 6:
                return WrenchEnemySocketLocalEuler;
            case 3:
            case 10:
                return PoleEnemySocketLocalEuler;
            case 8:
                return HammerEnemySocketLocalEuler;
            default:
                return Vector3.zero;
        }
    }

    public static void ApplyRuntimeOverrides(int level, GameObject sourcePrefab, GameObject weaponRoot)
    {
        if (sourcePrefab == null || weaponRoot == null)
            return;

        switch (Mathf.Clamp(level, 1, 16))
        {
            case 12:
                // Keep the imported saw materials so level 12 reads like the
                // reference hand saw: metal blade, warm handle, clear teeth.
                break;
        }
    }

    public static bool UsesRuntimeGripAnchor(int level, GameObject sourcePrefab)
    {
        return false;
    }

    public static bool IsChainsawLevel(int level, GameObject sourcePrefab)
    {
        if (Mathf.Clamp(level, 1, 16) != 12)
            return false;

        if (sourcePrefab == null)
            return true;

        string prefabName = sourcePrefab.name;
        return prefabName.IndexOf("saw", System.StringComparison.OrdinalIgnoreCase) >= 0
            || prefabName.IndexOf("chain", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static void ApplyChainsawPlayerGripPose(Transform weaponRoot)
    {
        ApplyWeaponTransform(weaponRoot, ChainsawPlayerLocalPosition, ChainsawPlayerLocalEuler);
    }

    public static void ApplyChainsawEnemyGripPose(Transform weaponRoot)
    {
        ApplyWeaponTransform(weaponRoot, ChainsawEnemyLocalPosition, ChainsawEnemyLocalEuler);
    }

    private static void ApplySawBoundsGripPose(Transform weaponRoot, Vector3 palmOffset, bool playerGrip)
    {
        if (weaponRoot == null)
            return;

        weaponRoot.localRotation = Quaternion.Euler(SawRuntimeMeshLocalEuler);

        Vector3 gripPoint;
        bool hasGripPoint = playerGrip
            ? TryGetSawPlayerGripPoint(weaponRoot, out gripPoint)
            : TryGetSawRuntimeGripPoint(weaponRoot, out gripPoint);

        if (!hasGripPoint)
        {
            ApplyWeaponTransform(
                weaponRoot,
                playerGrip ? ChainsawPlayerLocalPosition : ChainsawEnemyLocalPosition,
                SawRuntimeMeshLocalEuler);
            return;
        }

        Vector3 scaledGripPoint = Vector3.Scale(gripPoint, weaponRoot.localScale);
        weaponRoot.localPosition = palmOffset - (weaponRoot.localRotation * scaledGripPoint);
    }

    private static void ApplyWeaponTransform(Transform weaponRoot, Vector3 localPosition, Vector3 localEuler)
    {
        if (weaponRoot == null)
            return;

        weaponRoot.localPosition = localPosition;
        weaponRoot.localRotation = Quaternion.Euler(localEuler);
    }

    public static Transform GetOrCreateRuntimeGripAnchor(int level, GameObject sourcePrefab, Transform weaponSocket)
    {
        if (!UsesRuntimeGripAnchor(level, sourcePrefab) || weaponSocket == null)
            return weaponSocket;

        Transform gripAnchor = weaponSocket.Find(SawRuntimeSocketAnchorName);
        if (gripAnchor == null)
        {
            GameObject gripObject = new GameObject(SawRuntimeSocketAnchorName);
            gripAnchor = gripObject.transform;
            gripAnchor.SetParent(weaponSocket, worldPositionStays: false);
        }

        gripAnchor.localPosition = Vector3.zero;
        gripAnchor.localRotation = Quaternion.identity;
        gripAnchor.localScale = Vector3.one;
        return gripAnchor;
    }

    public static bool ApplyRuntimeGripPose(int level, GameObject sourcePrefab, Transform weaponRoot)
    {
        if (weaponRoot == null)
            return false;

        if (IsChainsawLevel(level, sourcePrefab))
        {
            ApplyChainsawEnemyGripPose(weaponRoot);
            return true;
        }

        if (!UsesRuntimeGripAnchor(level, sourcePrefab))
            return false;

        weaponRoot.localRotation = Quaternion.Euler(SawRuntimeMeshLocalEuler);
        weaponRoot.localPosition = Vector3.zero;

        Vector3 gripPoint = TryGetSawRuntimeGripPoint(weaponRoot, out Vector3 computedGripPoint)
            ? computedGripPoint
            : SawRuntimeFallbackGripPoint;

        Transform handleAnchor = GetOrCreateSawRuntimeHandleAnchor(weaponRoot);
        handleAnchor.localPosition = gripPoint;
        handleAnchor.localRotation = Quaternion.identity;
        handleAnchor.localScale = Vector3.one;

        Vector3 scaledGripPoint = Vector3.Scale(gripPoint, weaponRoot.localScale);
        weaponRoot.localPosition = -(weaponRoot.localRotation * scaledGripPoint);
        return true;
    }

    public static bool ApplyPlayerRuntimeGripPose(int level, GameObject sourcePrefab, Transform weaponRoot)
    {
        if (weaponRoot == null)
            return false;

        int clampedLevel = Mathf.Clamp(level, 1, 16);
        if (IsChainsawLevel(clampedLevel, sourcePrefab))
        {
            ApplyChainsawPlayerGripPose(weaponRoot);
            return true;
        }

        if (clampedLevel == 14
            && sourcePrefab != null
            && sourcePrefab.name.IndexOf("morgenstern", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (TryGetCombinedLocalBounds(weaponRoot, out Bounds localBounds))
            {
                weaponRoot.localRotation = GetMorgensternPlayerLocalRotation(localBounds);
                Vector3 gripPoint = GetPivotOpposedGripPoint(localBounds, MorgensternHandleGripInsetNormalized);
                Vector3 scaledGripPoint = Vector3.Scale(gripPoint, weaponRoot.localScale);
                weaponRoot.localPosition = MorgensternPlayerLocalPosition - (weaponRoot.localRotation * scaledGripPoint);
                return true;
            }

            weaponRoot.localRotation = Quaternion.Euler(0f, 0f, 180f);
            weaponRoot.localPosition = MorgensternPlayerLocalPosition;
            return true;
        }

        return ApplyRuntimeGripPose(level, sourcePrefab, weaponRoot);
    }

    private static Vector3 GetPivotOpposedGripPoint(Bounds localBounds, float insetNormalized)
    {
        int dominantAxis = GetDominantBoundsAxis(localBounds);
        bool handleExtendsTowardMax = DoesHandleExtendTowardMax(localBounds, dominantAxis);
        float min = localBounds.min[dominantAxis];
        float max = localBounds.max[dominantAxis];
        float gripNormalized = handleExtendsTowardMax
            ? insetNormalized
            : 1f - insetNormalized;

        Vector3 gripPoint = localBounds.center;
        gripPoint[dominantAxis] = Mathf.Lerp(min, max, gripNormalized);
        return gripPoint;
    }

    private static Quaternion GetMorgensternPlayerLocalRotation(Bounds localBounds)
    {
        int dominantAxis = GetDominantBoundsAxis(localBounds);
        bool handleExtendsTowardMax = DoesHandleExtendTowardMax(localBounds, dominantAxis);

        switch (dominantAxis)
        {
            case 0:
                // X-axis shaft: rotate so the head points down socket -Y.
                return Quaternion.Euler(0f, 0f, handleExtendsTowardMax ? 90f : -90f);
            case 1:
                // Y-axis shaft: only flip end-for-end when the head lives at +Y.
                return Quaternion.Euler(0f, 0f, handleExtendsTowardMax ? 0f : 180f);
            default:
                // Z-axis shaft: tilt the shaft forward into the player's strike axis.
                return Quaternion.Euler(handleExtendsTowardMax ? -90f : 90f, 0f, 0f);
        }
    }

    private static int GetDominantBoundsAxis(Bounds localBounds)
    {
        int dominantAxis = 0;
        float dominantSize = localBounds.size.x;
        if (localBounds.size.y > dominantSize)
        {
            dominantAxis = 1;
            dominantSize = localBounds.size.y;
        }
        if (localBounds.size.z > dominantSize)
            dominantAxis = 2;

        return dominantAxis;
    }

    private static bool DoesHandleExtendTowardMax(Bounds localBounds, int dominantAxis)
    {
        return Mathf.Abs(localBounds.max[dominantAxis]) >= Mathf.Abs(localBounds.min[dominantAxis]);
    }

    private static Transform GetOrCreateSawRuntimeHandleAnchor(Transform weaponRoot)
    {
        Transform handleAnchor = weaponRoot.Find(SawRuntimeHandleAnchorName);
        if (handleAnchor == null)
        {
            GameObject handleAnchorObject = new GameObject(SawRuntimeHandleAnchorName);
            handleAnchor = handleAnchorObject.transform;
            handleAnchor.SetParent(weaponRoot, worldPositionStays: false);
        }

        return handleAnchor;
    }

    private static bool TryGetSawRuntimeGripPoint(Transform weaponRoot, out Vector3 gripPoint)
    {
        gripPoint = SawRuntimeFallbackGripPoint;

        if (!TryGetCombinedLocalBounds(weaponRoot, out Bounds localBounds))
            return false;

        gripPoint = new Vector3(
            Mathf.Lerp(localBounds.min.x, localBounds.max.x, SawHandleGripXNormalized),
            Mathf.Lerp(localBounds.min.y, localBounds.max.y, SawHandleGripYNormalized),
            Mathf.Lerp(localBounds.min.z, localBounds.max.z, SawHandleGripZNormalized));
        return true;
    }

    private static bool TryGetSawPlayerGripPoint(Transform weaponRoot, out Vector3 gripPoint)
    {
        gripPoint = SawRuntimeFallbackGripPoint;

        if (weaponRoot == null)
            return false;

        if (!TryGetCombinedLocalBounds(weaponRoot, out Bounds localBounds))
            return false;

        gripPoint = new Vector3(
            Mathf.Lerp(localBounds.min.x, localBounds.max.x, SawPlayerHandleGripXNormalized),
            Mathf.Lerp(localBounds.min.y, localBounds.max.y, SawPlayerHandleGripYNormalized),
            Mathf.Lerp(localBounds.min.z, localBounds.max.z, SawPlayerHandleGripZNormalized));
        return true;
    }


    private static bool TryGetCombinedLocalBounds(Transform root, out Bounds combinedBounds)
    {
        combinedBounds = new Bounds();
        if (root == null)
            return false;

        bool hasBounds = false;
        Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;

        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            EncapsulateTransformedBounds(
                ref combinedBounds,
                ref hasBounds,
                meshFilter.sharedMesh.bounds,
                rootWorldToLocal * meshFilter.transform.localToWorldMatrix);
        }

        SkinnedMeshRenderer[] skinnedMeshes = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedMeshes.Length; i++)
        {
            SkinnedMeshRenderer skinnedMesh = skinnedMeshes[i];
            if (skinnedMesh == null)
                continue;

            Bounds sourceBounds = skinnedMesh.localBounds;
            if (sourceBounds.size.sqrMagnitude <= 0f && skinnedMesh.sharedMesh != null)
                sourceBounds = skinnedMesh.sharedMesh.bounds;

            if (sourceBounds.size.sqrMagnitude <= 0f)
                continue;

            EncapsulateTransformedBounds(
                ref combinedBounds,
                ref hasBounds,
                sourceBounds,
                rootWorldToLocal * skinnedMesh.transform.localToWorldMatrix);
        }

        return hasBounds;
    }

    private static void EncapsulateTransformedBounds(
        ref Bounds combinedBounds,
        ref bool hasBounds,
        Bounds sourceBounds,
        Matrix4x4 localToRoot)
    {
        Vector3 min = sourceBounds.min;
        Vector3 max = sourceBounds.max;

        Vector3[] corners =
        {
            new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, min.z),
            new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, min.z),
            new Vector3(max.x, max.y, max.z),
        };

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 point = localToRoot.MultiplyPoint3x4(corners[i]);
            if (!hasBounds)
            {
                combinedBounds = new Bounds(point, Vector3.zero);
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(point);
            }
        }
    }

    private static WeaponLoadout Create(float targetSize, params string[] resourcePaths)
    {
        return Create(
            targetSize,
            DefaultPlayerLocalPosition,
            DefaultPlayerLocalEuler,
            DefaultEnemyLocalPosition,
            DefaultEnemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreateShortGrip(float targetSize, params string[] resourcePaths)
    {
        return Create(
            targetSize,
            DefaultPlayerLocalPosition,
            DefaultPlayerLocalEuler,
            DefaultEnemyLocalPosition,
            DefaultEnemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreateMediumGrip(float targetSize, params string[] resourcePaths)
    {
        return Create(
            targetSize,
            MediumPlayerLocalPosition,
            DefaultPlayerLocalEuler,
            MediumEnemyLocalPosition,
            DefaultEnemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreateLongGrip(float targetSize, params string[] resourcePaths)
    {
        return Create(
            targetSize,
            LongPlayerLocalPosition,
            DefaultPlayerLocalEuler,
            LongEnemyLocalPosition,
            DefaultEnemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreatePoleGrip(float targetSize, params string[] resourcePaths)
    {
        return Create(
            targetSize,
            PolePlayerLocalPosition,
            DefaultPlayerLocalEuler,
            PoleEnemyLocalPosition,
            DefaultEnemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreatePlayerMatchedGrip(
        float targetSize,
        Vector3 localPosition,
        Vector3 localEuler,
        params string[] resourcePaths)
    {
        return Create(
            targetSize,
            localPosition,
            localEuler,
            localPosition,
            localEuler,
            resourcePaths);
    }

    private static WeaponLoadout CreateExactGrip(
        float targetSize,
        Vector3 playerLocalPosition,
        Vector3 playerLocalEuler,
        Vector3 enemyLocalPosition,
        Vector3 enemyLocalEuler,
        params string[] resourcePaths)
    {
        return Create(
            targetSize,
            playerLocalPosition,
            playerLocalEuler,
            enemyLocalPosition,
            enemyLocalEuler,
            resourcePaths);
    }

    private static WeaponLoadout Create(
        float targetSize,
        Vector3 playerLocalPosition,
        Vector3 playerLocalEuler,
        Vector3 enemyLocalPosition,
        Vector3 enemyLocalEuler,
        params string[] resourcePaths)
    {
        return new WeaponLoadout(
            resourcePaths,
            targetSize,
            playerLocalPosition,
            playerLocalEuler,
            enemyLocalPosition,
            enemyLocalEuler);
    }

    private static void ApplyBlackSawMaterialOverride(GameObject weaponRoot)
    {
        Color sawBlack = new Color(0.045f, 0.045f, 0.045f, 1f);
        Shader fallbackShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (fallbackShader == null)
            return;

        Renderer[] renderers = weaponRoot.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            if (renderer == null)
                continue;

            Material[] materials = renderer.materials;
            if (materials == null || materials.Length == 0)
                materials = new[] { new Material(fallbackShader) };

            for (int j = 0; j < materials.Length; j++)
            {
                Material source = materials[j];
                Material material = source != null ? new Material(source) : new Material(fallbackShader);

                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", null);
                if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", null);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", sawBlack);
                if (material.HasProperty("_Color")) material.SetColor("_Color", sawBlack);
                if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", Color.black);
                if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.12f);
                if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.18f);
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.18f);

                materials[j] = material;
            }

            renderer.materials = materials;
        }
    }

}
