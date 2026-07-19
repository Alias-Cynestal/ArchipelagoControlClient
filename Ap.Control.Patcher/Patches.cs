using System.Text;

namespace Ap.Control.Patcher
{
    /// <summary>The patches this tool knows how to apply. Adding one means adding a definition here.</summary>
    internal static class Patches
    {
        private const string Package = "ep100-000-generic";
        private const string Model = "g_ControlPointPrimeVisibilityState";

        /// <summary>
        /// Gate each elevator sector destination independently.
        ///
        /// The shipped panel decides its floor list from a single integer (the highest unlocked sector
        /// index), which makes the destinations a strict ladder — Maintenance can never be enabled
        /// without Research — and hardcodes the Pump Room entry as always available, a permanent back
        /// door into Maintenance. The per-sector information is collapsed before the UI ever sees it,
        /// so no amount of client-side work fixes it.
        ///
        /// Each condition is rewritten to read one bool from a UI model the client can write
        /// (ControlPointPrimeVisibilityState — see Memory/NativeUiModelController.cs):
        ///     m_bAbilitiesNew -> Research            m_bCraftingNew -> Maintenance (Lobby)
        ///     m_bTrialsNew    -> Containment         m_bOutfitsNew  -> Investigation
        ///     m_bAreControlPointsUpgraded -> Maintenance Pump Room
        ///
        /// Executive is deliberately untouched: it is the starting sector and the hub the player
        /// returns to, so gating it risks a softlock.
        ///
        /// Length neutrality is free here — elevatorShouldDisable(e,t,n) ignores its first two
        /// arguments, so every call collapses to a shorter '!cond' and the surplus is padded back.
        /// Cost: the control-point "new" badges become meaningless, since the client drives those bytes.
        /// </summary>
        internal static readonly PatchDef Elevator = new(
            Id: "elevator",
            Title: "Elevator sector gating",
            Package: Package,
            Target: "persistent.ui",
            Edits:
            [
                Edit.Of("Research",
                    "this.elevatorShouldDisable(e,u.ELEVATOR_RESEARCH_SECTOR,t>0)",
                    $"!{Model}.m_bAbilitiesNew"),
                Edit.Of("Maintenance",
                    "this.elevatorShouldDisable(e,u.ELEVATOR_MAINTENANCE_SECTOR,t>1)",
                    $"!{Model}.m_bCraftingNew"),
                Edit.Of("PumpRoom",
                    "this.elevatorShouldDisable(e,u.ELEVATOR_MAINTENANCE_SECTOR_PUMP_ROOM,!0)",
                    $"!{Model}.m_bAreControlPointsUpgraded"),
                Edit.Of("Containment",
                    "this.elevatorShouldDisable(e,u.ELEVATOR_CONTAINMENT_SECTOR,t>2)",
                    $"!{Model}.m_bTrialsNew"),
                Edit.Of("Investigation",
                    "t>3&&this.onAddSelectionItem(c.ELEVATOR_INVESTIGATION_SECTOR,"
                    + "this.elevatorShouldDisable(e,u.ELEVATOR_INVESTIGATION_SECTOR,!0)",
                    "this.onAddSelectionItem(c.ELEVATOR_INVESTIGATION_SECTOR,"
                    + $"!{Model}.m_bOutfitsNew"),
            ],
            // Anchored to the end of the last edit; a block comment there is syntactically inert.
            Balance: new PadBalancer(
                Encoding.ASCII.GetBytes($"!{Model}.m_bOutfitsNew"),
                "ap-control-elevator-patch-pad"));

        /// <summary>
        /// Remove weapon FORM unlocks from the control-point shop, leaving level upgrades alone.
        ///
        /// Archipelago hands out the forms as items, but the stock shop still sells them, so a player
        /// can craft what the multiworld was supposed to grant. Removing the whole Weapons tab would be
        /// too blunt: once AP grants a form, its upgrades should still be purchasable as in vanilla.
        ///
        /// Every shop recipe carries m_bIsUpgrade, set engine-side, and it splits exactly on that line
        /// ("Unlock SMG1" = false, "Upgrade SMG1 to SMG2" = true). It is live state rather than mock
        /// convention: CraftedScreenBehaviour branches on it in shipped code. Filtering the weapon list
        /// to m_bIsUpgrade therefore keeps upgrades and drops first-level unlocks.
        ///
        /// Grip needs no special case — internally Pistol=Grip, and Grip is owned from the start with no
        /// unlock recipe anywhere in the bundle. DLC forms are covered for free, since the filter keys
        /// off the flag rather than an enumerated list of forms.
        /// </summary>
        internal static readonly PatchDef ShopWeapons = new(
            Id: "shop",
            Title: "Shop weapon-form lockout",
            Package: Package,
            Target: "hud.ui",
            Edits:
            [
                Edit.Of("WeaponFirstLevelFilter",
                    "case a.ShopItemListType.WEAPON_UPGRADES:return this.weapons;",
                    "case a.ShopItemListType.WEAPON_UPGRADES:return this.weapons.filter(e=>e.m_bIsUpgrade);"),
            ],
            // This edit GROWS the file, so the bytes are bought back from a dead string literal in
            // ./game/shop/mocks.ts — browser-dev mock data, gated behind !isAttached and never executed
            // in the shipped game.
            Balance: new DonorBalancer(
                Encoding.ASCII.GetBytes("m_strName:\""),
                Encoding.ASCII.GetBytes("Upgrade Shotgun1 to Shotgun2"),
                Encoding.ASCII.GetBytes("\"")),
            Validate: NoRecordBoundaryBetweenEdits);

        /// <summary>
        /// hud.ui is a D34DB33F container, not a bare JS file. The byte trade above only works while
        /// both sites live in the SAME payload blob — if a record boundary fell between them, one inner
        /// resource would shrink and another grow, and both length prefixes would be wrong. Stock hud.ui
        /// keeps all its records in the header plus a trailer, so this holds; re-checking here means a
        /// game update that changes the layout makes the patch refuse rather than corrupt the archive.
        /// </summary>
        private static string? NoRecordBoundaryBetweenEdits(byte[] patched)
        {
            byte[] magic = [0x3F, 0xB3, 0x4D, 0xD3];
            int a = Bytes.IndexOf(patched, ShopWeapons.Edits[0].New);
            int b = Bytes.IndexOf(patched, ((DonorBalancer)ShopWeapons.Balance).Shrunk(
                ShopWeapons.Edits[0].New.Length - ShopWeapons.Edits[0].Old.Length));
            if (a < 0 || b < 0) return "could not locate both edit sites for the boundary check";

            (int lo, int hi) = a < b ? (a, b) : (b, a);
            return Bytes.IndexOf(patched[lo..hi], magic) >= 0
                ? "a D34DB33F record boundary now falls between the two edits; the byte trade would corrupt two resources"
                : null;
        }

        internal static readonly IReadOnlyList<PatchDef> All = [Elevator, ShopWeapons];

        internal static PatchDef? ById(string id)
            => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
