using System.Text;

namespace Ap.Control.Patcher
{
    /// <summary>The patches this tool knows how to apply. Adding one means adding a definition here.</summary>
    internal static class Patches
    {
        private const string Package = "ep100-000-generic";
        private const string Model = "g_ControlPointPrimeVisibilityState";

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
        /// resource would shrink and another grow, and both length prefixes would be wrong.
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

        internal static readonly PatchDef AbilitiesLock = new(
            Id: "abilities",
            Title: "Ability-upgrade menu lockout",
            Package: Package,
            Target: "hud.ui",
            Edits:
            [
                Edit.Of("AbilitiesSyncState",
                    "s.m_eAbilityUpgradeState=o.m_eAbilityUpgradeState",
                    "s.m_eAbilityUpgradeState=o.m_eAbilityUpgradeState===r.AbilityUpgradeState.UPGRADE_AVAILABLE"
                    + "?r.AbilityUpgradeState.UPGRADE_LOCKED:o.m_eAbilityUpgradeState"),
                Edit.Of("AbilitiesCloneState",
                    "m_eAbilityUpgradeState:e.m_eAbilityUpgradeState",
                    "m_eAbilityUpgradeState:e.m_eAbilityUpgradeState===r.AbilityUpgradeState.UPGRADE_AVAILABLE"
                    + "?r.AbilityUpgradeState.UPGRADE_LOCKED:e.m_eAbilityUpgradeState"),
                Edit.Of("AbilitiesTreeNeverLocked",
                    "isTreeLocked(e){if(!e)return!1;const t=this.getUnlockTypeForAbilityType(e.m_eAbilityUpgradeType);"
                    + "if(t)for(const e of this.abilities.keys()){const o=this.abilities.get(e);"
                    + "if(o&&o.m_eAbilityUpgradeType===t)return o.m_eAbilityUpgradeState!==r.AbilityUpgradeState.UPGRADE_BOUGHT}"
                    + "return!1}",
                    "isTreeLocked(e){return!1}"),
            ],

            Balance: new PadBalancer(
                Encoding.ASCII.GetBytes("isTreeLocked(e){return!1}"),
                "ap-control-abilities-patch-pad"));

        internal static readonly IReadOnlyList<PatchDef> All = [Elevator, ShopWeapons, AbilitiesLock];

        internal static PatchDef? ById(string id)
            => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
