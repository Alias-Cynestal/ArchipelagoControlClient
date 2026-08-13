using System.Text;

namespace Ap.Control.Patcher
{
    /// <summary>The patches this tool knows how to apply. Adding one means adding a definition here.</summary>
    internal static class Patches
    {
        private const string Package = "ep100-000-generic";
        private const string Model = "g_ControlPointPrimeVisibilityState";

        internal static readonly PatchDef ElevatorBits = new(
            Id: "elevator-bits",
            Title: "Elevator sector gating (legacy, repurposes the control-point badges)",
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
        /// Elevator gating, driven by the client over the UI bridge.
        ///
        /// Replaces the original approach, which repurposed the five "new" badge bools on
        /// g_ControlPointPrimeVisibilityState and poked them into process memory ten times a second.
        /// That cost the badges, capped the scheme at five destinations, and needed a vtable scan to
        /// find the model. The elevator UI turns out to live in persistent.ui, which shares one
        /// webpack runtime — and therefore one JS context — with menu.ui, where the bootstrap already
        /// runs. So the client can simply set a global and the gate can read it.
        ///
        /// <c>elevatorShouldDisable(e,t,n)</c> ignores its first two arguments and returns
        /// <c>!n</c>. Since <c>t</c> is the sector id (0 EXECUTIVE, 1 RESEARCH, 2 MAINTENANCE,
        /// 3 PUMP_ROOM, 4 CONTAINMENT, 5 INVESTIGATION), gating every destination is one edit to that
        /// one method rather than five edits at the call sites — and it covers EXECUTIVE and
        /// PUMP_ROOM, which the bit scheme could not reach.
        ///
        /// With no client running, <c>window.APEV</c> is undefined and the method falls through to
        /// the game's own logic, so a patched install still plays normally.
        /// </summary>
        internal static readonly PatchDef Elevator2 = new(
            Id: "elevator",
            Title: "Elevator sector gating (client-driven)",
            Package: Package,
            Target: "persistent.ui",
            Edits:
            [
                Edit.Of("ElevatorGate",
                    "elevatorShouldDisable(e,t,n){return!n}",
                    "elevatorShouldDisable(e,t,n){return window.APEV?!APEV[t]:!n}"),

                // Investigation is not even listed unless the save's floor counter is past 3. It has
                // to be listed for the gate above to have anything to say about it.
                Edit.Of("InvestigationAlwaysListed",
                    "t>3&&this.onAddSelectionItem(c.ELEVATOR_INVESTIGATION_SECTOR,",
                    "this.onAddSelectionItem(c.ELEVATOR_INVESTIGATION_SECTOR,"),
            ],
            // 17 bytes short. Paid for out of one of the five mock notification payloads in
            // ./northlight/narrativeMocks.ts, the whole module being behind `o.isAttached||(...)`.
            // NOT out of mockSystemInfo, which looks equally dead but is read unconditionally by the
            // ActionKey view-model's constructor.
            Balance: new CollapseBalancer(
                Encoding.ASCII.GetBytes("\"notificationNarrative3\",()=>{o.trigger(\"OnNotification\",{"),
                "ap-control-elevator-pad"));

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

        /// <summary>
        /// The dead region both menu.ui patches are paid for out of: 37 KB of mock keybind rows in
        /// ./game/main-menu/mocks.ts, behind `if(a.isAttached)return`. Four option mocks end with an
        /// empty keybind list, so the marker has to carry the whole run of empty arrays to be unique.
        /// </summary>
        private static readonly byte[] MenuMockKeyBinds = Encoding.ASCII.GetBytes(
            "m_vecBoolProperties:[],m_vecStrProperties:[],"
            + "m_vecDisplayModeProperties:[],m_vecKeyBindProperties:[");

        /// <summary>
        /// Same hazard as the shop patch: the byte trade only works while the injected payload and the
        /// region it is paid for out of live in the same D34DB33F payload blob.
        /// </summary>
        private static string? NoRecordBoundaryBetween(byte[] patched, string payload, byte[] marker)
        {
            byte[] magic = [0x3F, 0xB3, 0x4D, 0xD3];
            int a = Bytes.IndexOf(patched, Encoding.ASCII.GetBytes(payload));
            int b = Bytes.IndexOf(patched, marker);
            if (a < 0 || b < 0) return "could not locate both patch sites for the boundary check";

            (int lo, int hi) = a < b ? (a, b) : (b, a);
            return Bytes.IndexOf(patched[lo..hi], magic) >= 0
                ? "a D34DB33F record boundary falls between the injection site and the freed region"
                : null;
        }

        /// <summary>
        /// The permanent injection point. Deliberately tiny and final: it contains no UI of its own,
        /// it just connects to the client and runs whatever JavaScript the client sends. Every menu,
        /// every tracker, every later change then lives in <c>ui/</c> as ordinary source, and reaching
        /// the game costs a reconnect instead of a repack. The view has both WebSocket and DOM key
        /// events, which is what makes that possible at all.
        ///
        /// Consequences worth knowing:
        ///   - Every message is JavaScript, evaluated in the view. Data updates arrive as calls the
        ///     loaded code defines (e.g. `AP.update({...})`), so there is only one message type.
        ///   - It reconnects with backoff forever, so the game and the client can start in either
        ///     order, and a client restart does not need a game restart.
        ///   - The port is bound to 127.0.0.1 by the client. Any local process could answer it first
        ///     and would then be running code inside the game, which is acceptable for a local mod but
        ///     is the reason this must never be pointed at a non-loopback address.
        /// </summary>
        private const string BootstrapPayload =
            "!function(){"
            + "var V=\"menu\",U=\"ws://127.0.0.1:38381\",d=500,s=null,E=!0;"
            // Probe whether eval is usable ONCE. Testing it per-message would mean a payload that
            // throws gets run twice by the fallback.
            + "try{(0,eval)(\"1\")}catch(e){E=!1}"
            + "function R(c){try{E?(0,eval)(c):new Function(c)()}"
            + "catch(e){try{s.send(\"error:\"+e)}catch(_){}}}"
            + "function C(){"
            + "try{s=new WebSocket(U)}catch(e){return B()}"
            + "s.onopen=function(){d=500,s.send(\"hello:\"+V)},"
            + "s.onmessage=function(e){R(e.data)},"
            + "s.onclose=function(){s=null,B()},"
            + "s.onerror=function(){}}"
            + "function B(){setTimeout(C,d),d=Math.min(2*d,5e3)}"
            + "window.APView=V,"
            + "window.APSend=function(m){try{s&&1===s.readyState&&s.send(m)}catch(e){}},"
            + "C()}()";

        internal static readonly PatchDef Bootstrap = new(
            Id: "bootstrap",
            Title: "Archipelago UI bootstrap",
            Package: Package,
            // menu.ui is both the main menu and the pause menu, so one injection covers the connect
            // screen and an in-game panel. hud.ui gets its own bootstrap when the tracker needs it.
            Target: "menu.ui",
            Edits:
            [
                Edit.Of("MainMenuInit",
                    "c.default(),r.default.subscribeToModel()",
                    $"c.default(),{BootstrapPayload},r.default.subscribeToModel()"),
            ],
            Balance: new CollapseBalancer(MenuMockKeyBinds, "ap-control-bootstrap-pad"),
            Validate: patched => NoRecordBoundaryBetween(patched, BootstrapPayload, MenuMockKeyBinds));

        /// <summary>The gameplay patches — what "all" means, and what a player is meant to install.</summary>
        internal static readonly IReadOnlyList<PatchDef> All = [Elevator2, ShopWeapons, AbilitiesLock, Bootstrap];

        /// <summary>
        /// Selectable by name but deliberately outside <see cref="All"/>: diagnostics answer a question
        /// during development and are restored afterwards, so "apply all" must never install one.
        /// </summary>
        internal static readonly IReadOnlyList<PatchDef> Diagnostics = [ElevatorBits];

        internal static PatchDef? ById(string id)
            => All.Concat(Diagnostics)
                  .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
