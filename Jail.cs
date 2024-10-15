using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

//using Oxide.Ext.Discord.Libraries;

namespace Oxide.Plugins
{
    [Info("Jail", "RocketMyrr", "5.0.0")]
    [Description("An optimized jail system for Rust")]
    public class Jail : RustPlugin
    {
        #region Fields

        private const string JailPermission = "jail.use";
        private const string AdminPermission = "jail.admin";

        //private readonly DiscordLink _link = Interface.Oxide.GetLibrary<DiscordLink>();

        private List<ulong> jailedPlayers = new List<ulong>(); // Store player IDs
        private Dictionary<ulong, DateTime> releaseTimes = new Dictionary<ulong, DateTime>();
        private Dictionary<ulong, TimeSpan> remainingJailTimes = new Dictionary<ulong, TimeSpan>(); // To store remaining jail time
        private Dictionary<ulong, string> jailReasons = new Dictionary<ulong, string>(); // To store jail reasons
        private Dictionary<ulong, Vector3> previousLocations = new Dictionary<ulong, Vector3>(); // Save player's previous locations

        private const string SubfolderName = "Jail";

        private StoredData storedData;

        [PluginReference]
        private Plugin ZoneManager, NTeleportation, Spawns;

        #endregion Fields

        #region Configuration

        private ConfigData configData;

        private class ConfigData
        {
            [JsonProperty(PropertyName = "Rules URL for UI message")]
            public string RulesUrl { get; set; }

            [JsonProperty(PropertyName = "Jail Zone ID")]
            public string JailZoneId { get; set; }

            [JsonProperty(PropertyName = "Default Jail Time (if none provided)")]
            public int defaultJailTime { get; set; }

            [JsonProperty(PropertyName = "Blacklisted commands for prisoners")]
            public string[] CommandBlacklist { get; set; }

            [JsonProperty(PropertyName = "WebHook for Discord message on Jail")]
            public string webhook { get; set; }

            public Oxide.Core.VersionNumber Version { get; set; }
        }

        private ConfigData GetBaseConfig()
        {
            return new ConfigData
            {
                RulesUrl = "https://rustempires.com/",
                JailZoneId = "jail",
                defaultJailTime = 15,
                CommandBlacklist = new string[] { "tp", "event", "tpa", "tpr", "s", "kill", "pm", "r", "clan", "c", "s", "ad", "etp", "global.glassbridge_ui join", "glassbridge join", "info", "advert", "ui.floordrops.btn_click", "survival", "map", "pbjoin", "race" },
                webhook = "https://discord.com/api/webhooks/872148083457269790/bYonIhzw_lCT_CR7uIr8sP39iwB0WkcFirQanbPsbEFscfvlRwXrnmLWv5RQDymf3mn_",
            };
        }

        protected override void LoadDefaultConfig() => configData = GetBaseConfig();

        protected override void SaveConfig() => Config.WriteObject(configData, true);

        protected override void LoadConfig()
        {
            base.LoadConfig();
            configData = Config.ReadObject<ConfigData>();

            if (configData.Version < Version)
                UpdateConfigValues();

            Config.WriteObject(configData, true);
        }

        private void UpdateConfigValues()
        {
            PrintWarning("Config update detected! Updating config values...");

            ConfigData baseConfig = GetBaseConfig();
            /*if (configData.Version < new Core.VersionNumber(1, 2, 0))
            {
                configData.Location.Monuments = baseConfig.Location.Monuments;
            }*/

            configData.Version = Version;
            PrintWarning("Config update completed!");
        }

        #endregion Configuration

        #region Data Storage

        private class StoredData
        {
            public List<JailedPlayer> jailedPlayers = new List<JailedPlayer>();
            public Dictionary<ulong, TimeSpan> remainingJailTimes = new Dictionary<ulong, TimeSpan>();
            public Dictionary<ulong, string> jailReasons = new Dictionary<ulong, string>();
            public Dictionary<ulong, Vector3> previousLocations = new Dictionary<ulong, Vector3>(); // Save previous locations in data
        }

        private class JailedPlayer
        {
            public ulong playerId;
            public DateTime releaseTime;
            public string jailReason;
        }

        private void SaveData()
        {
            foreach (var playerId in jailedPlayers)
            {
                if (releaseTimes.ContainsKey(playerId))
                {
                    TimeSpan remainingTime = releaseTimes[playerId] - DateTime.UtcNow;
                    remainingJailTimes[playerId] = remainingTime;
                }
            }
            storedData.remainingJailTimes = remainingJailTimes;
            storedData.jailReasons = jailReasons;
            storedData.previousLocations = previousLocations; // Save previous locations
            Interface.Oxide.DataFileSystem.WriteObject($"{SubfolderName}/{Name}", storedData);
        }

        private void LoadData()
        {
            // Read the stored data or create new if null
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>($"{SubfolderName}/{Name}") ?? new StoredData();

            // Initialize the collections from stored data
            remainingJailTimes = new Dictionary<ulong, TimeSpan>(storedData.remainingJailTimes);  // Avoid modifying the original dictionary
            jailReasons = new Dictionary<ulong, string>(storedData.jailReasons);
            previousLocations = new Dictionary<ulong, Vector3>(storedData.previousLocations);

            // Temporary dictionary for storing any changes to jail times
            Dictionary<ulong, TimeSpan> tempRemainingJailTimes = new Dictionary<ulong, TimeSpan>();

            // Iterate over remaining jail times safely
            foreach (var kvp in storedData.remainingJailTimes)
            {
                jailedPlayers.Add(kvp.Key);  // Add the jailed player to the list
                tempRemainingJailTimes[kvp.Key] = kvp.Value;  // Store the unchanged jail time temporarily
            }

            remainingJailTimes = tempRemainingJailTimes;
        }

        #endregion Data Storage

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(JailPermission, this);
            permission.RegisterPermission(AdminPermission, this);
            LoadData();
            RestoreJailTimers();
        }

        private void Unload()
        {
            SaveData();
            foreach (var player in BasePlayer.activePlayerList)
                if (jailedPlayers.Contains(player.userID))
                {
                    OnPlayerDisconnected(player, "Unloaded");
                    UnlockPlayerInventory(player);
                }
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                TeleportToJail(player);
                LockPlayerInventory(player);
                ShowJailUI(player);
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                TimeSpan remainingTime = releaseTimes[player.userID] - DateTime.UtcNow;
                remainingJailTimes[player.userID] = remainingTime;
                releaseTimes.Remove(player.userID);
                SaveData();
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.In(1, () => OnPlayerConnected(player));
                return;
            }
            if (remainingJailTimes.ContainsKey(player.userID))
            {
                TimeSpan remainingTime = remainingJailTimes.ContainsKey(player.userID) ? remainingJailTimes[player.userID] : TimeSpan.Zero;

                releaseTimes[player.userID] = DateTime.UtcNow.Add(remainingTime);
                remainingJailTimes.Remove(player.userID);

                ScheduleReleaseTimer(player.userID, remainingTime);
                TeleportToJail(player);
                LockPlayerInventory(player);
                ShowJailUI(player);
                player.ChatMessage($"[<color=#FF4F4B>Jail</color>] Your jail time has resumed. You have {remainingTime.TotalMinutes:F2} minutes remaining.");
                Puts($"{player} was jailed offline and has come online Admin and has {remainingTime.TotalMinutes:F2} minutes remaining.");
            }
        }

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (player == null)
                return null;

            if (IsPrisoner(player))
            {
                Puts($"[Jailed Chat Log] {player}: {message}");
                SendStaffMessage(player.displayName, message);
                return true;
            }
            return null;
        }

        private bool IsCommandBlocked(string command, string[] args)
        {
            List<string> blacklist = new List<string>(configData.CommandBlacklist);
            if (blacklist.Contains(command, StringComparer.OrdinalIgnoreCase)
                || blacklist.Contains(string.Concat(command, args), StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            string splitCommand = command.Substring(command.IndexOf(".", StringComparison.Ordinal) + 1);
            if (blacklist.Contains(splitCommand, StringComparer.OrdinalIgnoreCase)
                || blacklist.Contains(string.Concat(splitCommand, args), StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private object OnPlayerCommand(BasePlayer player, string command, string[] args)
        {
            if (player == null || player.IsAdmin)
                return null;

            if (IsPrisoner(player))
            {
                if (IsCommandBlocked(command, args))
                {
                    SendReply(player, "[<color=#FF4F4B>Jail</color>] You can not use that command whilst in jail");
                    return false;
                }
            }
            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Connection?.player as BasePlayer;

            if (player == null || player.IsAdmin)
                return null;

            if (IsPrisoner(player))
            {
                if (IsCommandBlocked(arg.cmd.FullName, Oxide.Game.Rust.Libraries.Covalence.RustCommandSystem.ExtractArgs(arg)))
                {
                    SendReply(player, "[<color=#FF4F4B>Jail</color>] You can not use that command whilst in jail");
                    return false;
                }
            }

            return null;
        }

        private object OnPlayerInput(BasePlayer player, InputState input)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                if (input.WasJustPressed(BUTTON.FIRE_PRIMARY) || input.WasJustPressed(BUTTON.FIRE_SECONDARY))
                {
                    return true; // Cancels the player's input if they're trying to use an item in the hotbar
                }
            }

            return null;
        }

        private object CanUseLockedEntity(BasePlayer player, BaseLock entity)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                return false; // Denies access to locked entities like doors or items
            }

            return null;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var player = info?.Initiator as BasePlayer;
            if (player != null && jailedPlayers.Contains(player.userID))
            {
                return true; // Prevents the attacked player from taking damage
            }
            return null;
        }

        private object OnPlayerThrowItem(BasePlayer player, Item item)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                return true; // Prevents the player from throwing items
            }
            return null;
        }

        private object OnWeaponReload(BaseProjectile weapon, BasePlayer player)
        {
            if (IsPrisoner(player)) return false;
            return null;
        }

        private object OnMagazineReload(BaseProjectile weapon, int desiredAmount, BasePlayer player)
        {
            if (IsPrisoner(player)) return false;
            return null;
        }

        private object OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (IsPrisoner(attacker)) return false;
            return null;
        }

        private object CanUseItem(Item item, BasePlayer player)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                return false; // Prevents item use, including food, syringes, etc.
            }
            return null;
        }

        private object OnPlayerActiveItemChanged(BasePlayer player, Item oldItem, Item newItem)
        {
            if (jailedPlayers.Contains(player.userID))
            {
                return true; // Cancels the item switch
            }
            return null;
        }

        private object CanMoveItem(Item item, PlayerInventory playerLoot, uint targetContainer, int targetSlot, bool willStack)
        {
            BasePlayer player = playerLoot.baseEntity;
            if (player != null && jailedPlayers.Contains(player.userID))
            {
                return false; // Prevents moving items in the inventory
            }
            return null;
        }

        #endregion Hooks

        #region Jail Management
        private void JailOfflinePlayer(ulong playerId, TimeSpan? jailTime = null, string reason = "No reason provided")
        {
            var releaseTime = jailTime.HasValue ? DateTime.UtcNow.Add(jailTime.Value) : DateTime.UtcNow.Add(TimeSpan.FromMinutes(configData.defaultJailTime));
            // Store offline player's jail data
            storedData.jailedPlayers.Add(new JailedPlayer
            {
                playerId = playerId,
                releaseTime = releaseTime,
                jailReason = reason
            });
            TimeSpan remainingTime = releaseTime - DateTime.UtcNow;
            remainingJailTimes[playerId] = remainingTime;
            SaveData();
        }

        private void RestoreJailTimers()
        {
            foreach (var playerId in remainingJailTimes.Keys)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player != null && player.IsConnected)
                {
                    TimeSpan remainingTime = remainingJailTimes[playerId];
                    releaseTimes[playerId] = DateTime.UtcNow.Add(remainingTime);
                    ScheduleReleaseTimer(playerId, remainingTime);
                    LockPlayerInventory(player);
                    ShowJailUI(player);
                }
            }
        }

        private void JailPlayer(BasePlayer player, TimeSpan? jailTime = null, string reason = "No reason provided")
        {
            if (jailedPlayers.Contains(player.userID)) return;

            jailedPlayers.Add(player.userID);
            var releaseTime = jailTime.HasValue ? DateTime.UtcNow.Add(jailTime.Value) : DateTime.UtcNow.Add(TimeSpan.FromMinutes(configData.defaultJailTime));
            releaseTimes[player.userID] = releaseTime;
            jailReasons[player.userID] = reason;

            storedData.jailedPlayers.Add(new JailedPlayer
            {
                playerId = player.userID,
                releaseTime = releaseTime,
                jailReason = reason
            });

            SaveData();

            SavePlayerLocation(player); // Save player's previous location
            LockPlayerInventory(player);

            TeleportToJail(player);
            NextTick(() =>
            {
                ShowJailUI(player);
            });
            player.ChatMessage($"[<color=#FF4F4B>Jail</color>] You have been jailed for: {reason} for {jailTime?.TotalMinutes:F2} minutes.");

            // Schedule release timer
            ScheduleReleaseTimer(player.userID, jailTime.HasValue ? jailTime.Value : TimeSpan.FromMinutes(configData.defaultJailTime));
        }

        private void ReleasePlayer(ulong playerId)
        {
            if (!jailedPlayers.Contains(playerId)) return;

            jailedPlayers.Remove(playerId);
            releaseTimes.Remove(playerId);
            remainingJailTimes.Remove(playerId);
            jailReasons.Remove(playerId);

            storedData.jailedPlayers.RemoveAll(p => p.playerId == playerId);
            SaveData();

            var player = BasePlayer.FindByID(playerId);
            if (player != null)
            {
                TeleportBackToPreviousLocation(player); // Teleport player back to their saved location
                UnlockPlayerInventory(player);
                DestroyJailUI(player);
                player.ChatMessage("[<color=#FF4F4B>Jail</color>] You have been released from jail.");
            }
        }

        private void ExtendJailTime(ulong playerId, TimeSpan extension)
        {
            if (!jailedPlayers.Contains(playerId)) return;

            releaseTimes[playerId] = releaseTimes[playerId].Add(extension);

            var player = BasePlayer.FindByID(playerId);
            if (player != null)
            {
                TimeSpan remainingTime = releaseTimes[playerId] - DateTime.UtcNow;
                ShowJailUI(player); // Update UI with the new remaining time
                player.ChatMessage($"Your jail time has been extended. New remaining time: {remainingTime.Hours:D2}:{remainingTime.Minutes:D2}:{remainingTime.Seconds:D2}.");
            }

            SaveData(); // Persist changes
        }

        private void TeleportToJail(BasePlayer player)
        {
            var spawnLocation = GetSpawnLocation();
            if (spawnLocation is Vector3)
            {
                player.PauseFlyHackDetection(5f);
                player.PauseSpeedHackDetection(5f);
                player.UpdateActiveItem(default);
                player.EnsureDismounted();
                player.Server_CancelGesture();

                if (player.HasParent())
                {
                    player.SetParent(null, true, true);
                }

                if (player.IsConnected)
                {
                    StartSleeping(player);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);
                    player.ClientRPC(RpcTarget.Player("StartLoading", player), arg1: true);
                }
                Vector3 position = (Vector3)spawnLocation;
                position.y += 0.1f;
                player.Teleport(position);
                if (player.IsConnected)
                {
                    if (!player._limitedNetworking)
                    {
                        player.UpdateNetworkGroup();
                        player.SendNetworkUpdateImmediate(false);
                    }

                    player.ClearEntityQueue(null);
                    player.SendFullSnapshot();
                    if (CanWake(player)) player.Invoke(() =>
                    {
                        if (player && player.IsConnected)
                        {
                            if (player.limitNetworking) EndSleeping(player);
                            else player.EndSleeping();
                        }
                    }, 0.5f);
                }
            }
        }

        private bool IsPlayerInJailZone(BasePlayer player)
        {
            if (ZoneManager == null) return false;

            object inZone = ZoneManager?.Call("isPlayerInZone", configData.JailZoneId, player);
            return inZone is bool && (bool)inZone;
        }

        private void ScheduleReleaseTimer(ulong playerId, TimeSpan jailTime)
        {
            // Schedule a one-time timer that triggers when the player's jail time is up
            timer.Once((float)jailTime.TotalSeconds, () =>
            {
                ReleasePlayer(playerId);
            });
        }

        #endregion Jail Management

        #region API Methods

        private bool IsPrisoner(BasePlayer player) => jailedPlayers.Contains(player.userID);

        private bool API_IsPrisoner(BasePlayer player) => jailedPlayers.Contains(player.userID);

        #endregion API Methods

        #region Player Location Saving and Restoring

        private void SavePlayerLocation(BasePlayer player)
        {
            if (player != null)
            {
                previousLocations[player.userID] = player.transform.position;
                SaveData(); // Persist location data
            }
        }

        private void TeleportBackToPreviousLocation(BasePlayer player)
        {
            if (player != null && previousLocations.ContainsKey(player.userID))
            {
                player.PauseFlyHackDetection(5f);
                player.PauseSpeedHackDetection(5f);
                player.UpdateActiveItem(default);
                player.EnsureDismounted();
                player.Server_CancelGesture();

                if (player.HasParent())
                {
                    player.SetParent(null, true, true);
                }

                if (player.IsConnected)
                {
                    StartSleeping(player);
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);
                    player.ClientRPC(RpcTarget.Player("StartLoading", player), arg1: true);
                }
                Vector3 position = previousLocations[player.userID];
                position.y += 0.1f;
                player.Teleport(position);
                if (player.IsConnected)
                {
                    if (!player._limitedNetworking)
                    {
                        player.UpdateNetworkGroup();
                        player.SendNetworkUpdateImmediate(false);
                    }

                    player.ClearEntityQueue(null);
                    player.SendFullSnapshot();
                    if (CanWake(player)) player.Invoke(() =>
                    {
                        if (player && player.IsConnected)
                        {
                            if (player.limitNetworking) EndSleeping(player);
                            else player.EndSleeping();
                        }
                    }, 0.5f);
                }
                previousLocations.Remove(player.userID);
                SaveData(); // Remove saved location after teleport
            }
        }

        #endregion Player Location Saving and Restoring

        #region Inventory Lock/Unlock Methods

        private void LockPlayerInventory(BasePlayer player)
        {
            if (player == null) return;

            player.inventory.containerMain.SetFlag(ItemContainer.Flag.IsLocked, true);
            player.inventory.containerBelt.SetFlag(ItemContainer.Flag.IsLocked, true);
            player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, true);
        }

        private void UnlockPlayerInventory(BasePlayer player)
        {
            if (player == null) return;

            player.inventory.containerMain.SetFlag(ItemContainer.Flag.IsLocked, false);
            player.inventory.containerBelt.SetFlag(ItemContainer.Flag.IsLocked, false);
            player.inventory.containerWear.SetFlag(ItemContainer.Flag.IsLocked, false);
        }

        #endregion Inventory Lock/Unlock Methods

        #region UI Methods

        private void ShowJailUI(BasePlayer player)
        {
            CuiElementContainer elements = new CuiElementContainer();
            string panel = elements.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.8" },
                RectTransform = { AnchorMin = "0.25 0.25", AnchorMax = "0.75 0.75" },
                CursorEnabled = true
            }, "Overlay", "JailUIPanel");

            TimeSpan remainingTime = releaseTimes[player.userID] - DateTime.UtcNow;
            string reason = jailReasons.ContainsKey(player.userID) ? jailReasons[player.userID] : "No reason provided";

            // Jail UI Message
            elements.Add(new CuiLabel
            {
                Text = { Text = $"You were jailed for: {reason}\nTime remaining: {remainingTime.TotalMinutes:F2} minutes\nA Staff member will come talk to you. While you are waiting please read the rules at: {configData.RulesUrl}", FontSize = 22, Align = TextAnchor.MiddleCenter },
                RectTransform = { AnchorMin = "0.1 0.3", AnchorMax = "0.9 0.7" }
            }, panel);

            // Close button
            elements.Add(new CuiButton
            {
                Button = { Color = "0.8 0.1 0.1 0.8", Close = "JailUIPanel" },
                RectTransform = { AnchorMin = "0.45 0.1", AnchorMax = "0.55 0.2" },
                Text = { Text = "Close", FontSize = 20, Align = TextAnchor.MiddleCenter }
            }, panel);

            CuiHelper.AddUi(player, elements);
        }

        private void DestroyJailUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "JailUIPanel");
        }

        #endregion UI Methods

        #region Commands

        [ChatCommand("jail")]
        private void JailCommand(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, AdminPermission))
            {
                player.ChatMessage("You do not have permission to use this command.");
                return;
            }

            if (args.Length < 1)
            {
                player.ChatMessage("[<color=#FF4F4B>Jail</color>]\n");
                player.ChatMessage("/jail send <playername/id> [minutes:optional:15m] [reason:optional] - Send to Jail\n");
                player.ChatMessage("/jail free <playername/id> - Release from Jail\n");
                player.ChatMessage("/jail extend <playername/id> [minutes:optional] - Extend Jail Time\n");
                return;
            }

            switch (args[0].ToLower())
            {
                case "send":
                    if (args.Length >= 2)
                    {
                        BasePlayer targetPlayer = FindPlayersSingle(args[1], player);
                        bool offline = false;
                        ulong targetId = 0;
                        if (targetPlayer == null || !targetPlayer.IsConnected)
                        {

                            if (targetPlayer.userID.IsSteamId() && !targetPlayer.IsConnected)
                            {
                                if (storedData.jailedPlayers.Any(jp => jp.playerId == targetId))
                                {
                                    player.ChatMessage("Player is already jailed.");
                                    return;
                                }

                                offline = true;
                            }
                            else
                            {
                                player.ChatMessage("Player not found.");
                                return;
                            }
                        }

                        if (storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            player.ChatMessage("Player is already jailed, use /jail extend to extend time.");
                            return;
                        }

                        TimeSpan jailTime = TimeSpan.FromMinutes((double)configData.defaultJailTime);
                        string reason = "No reason provided";
                        if (args.Length > 2)
                        {
                            int.TryParse(args[2], out int minutes);
                            jailTime = TimeSpan.FromMinutes(minutes);
                        }

                        if (args.Length > 3)
                        {
                            var reasons = new StringBuilder();
                            for (int i = 3; i < args.Length; i++)
                                reasons.Append(" " + args[i]);
                            reason = reasons.ToString();
                        }

                        if (offline)
                        {
                            JailOfflinePlayer(targetId, jailTime, reason);
                            player.ChatMessage($"Offline player {targetId} has been jailed.");
                            Puts($"Player {targetPlayer.name} was sent to jail for {jailTime.TotalMinutes:F2} minutes because {reason} by {player.displayName} while offline");
                        }
                        else
                        {
                            SendToDiscordBot(targetPlayer, player, reason, jailTime.TotalMinutes.ToString());
                            JailPlayer(targetPlayer, jailTime, reason);
                            player.ChatMessage($"[<color=#FF4F4B>Jail</color>] You have sent {targetPlayer.displayName} to jail for {jailTime.TotalMinutes:F2} minutes because {reason}");
                            PrintToChat($"[<color=#FF4F4B>Jail</color>] Player {targetPlayer.displayName} was sent to jail for {jailTime.TotalMinutes:F2} minutes because {reason}");
                            Puts($"Player {targetPlayer.displayName} was sent to jail for {jailTime.TotalMinutes:F2} minutes because {reason} by {player.displayName}");
                        }

                    }
                    return;

                case "free":
                    if (args.Length >= 2)
                    {
                        BasePlayer targetPlayer = FindPlayersSingle(args[1], player);
                        if (targetPlayer == null)
                        {
                            player.ChatMessage("Player not found.");
                            return;
                        }
                        if (!storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            player.ChatMessage("Player is not jailed.");
                            return;
                        }

                        ReleasePlayer(targetPlayer.userID);
                        player.ChatMessage($"[<color=#FF4F4B>Jail</color>] You have released {targetPlayer.displayName} from jail.");
                        Puts($"Player {targetPlayer.displayName} was released from jail by {player.displayName}");
                    }
                    return;

                case "extend":
                    if (args.Length >= 2)
                    {
                        int extraMinutes = int.MaxValue;
                        if (args.Length > 2)
                        {
                            extraMinutes = int.Parse(args[2]);
                        }

                        BasePlayer targetPlayer = FindPlayersSingle(args[1], player);
                        if (targetPlayer == null)
                        {
                            player.ChatMessage("Player not found.");
                            return;
                        }
                        if (!storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            player.ChatMessage("Player is not jailed.");
                            return;
                        }

                        ExtendJailTime(targetPlayer.userID, TimeSpan.FromMinutes(extraMinutes));
                        player.ChatMessage($"[<color=#FF4F4B>Jail</color>] You have extended the jail time for {targetPlayer.displayName} by {extraMinutes} minutes.");
                        Puts($"Player {targetPlayer.displayName} was extended jail time by {extraMinutes} minutes by {player.displayName}");
                    }
                    return;

                case "status":
                    List<string> jailedPlayersInfo = new List<string>();

                    foreach (var kvp in storedData.jailedPlayers)
                    {
                        BasePlayer jailedPlayer = BasePlayer.FindByID(kvp.playerId);
                        if (jailedPlayer != null && jailedPlayer.IsConnected)
                        {
                            TimeSpan remainingTime = releaseTimes[jailedPlayer.userID] - DateTime.UtcNow;
                            string formattedTime = $"{remainingTime.Days}{remainingTime.Hours:D2}:{remainingTime.Minutes:D2}:{remainingTime.Seconds:D2}";

                            jailedPlayersInfo.Add($"<color=orange>{jailedPlayer.displayName}</color> - {formattedTime} remaining, Reason: {kvp.jailReason}");
                        }
                    }

                    if (jailedPlayersInfo.Count == 0)
                    {
                        player.ChatMessage("There are no players currently jailed online.");
                    }
                    else
                    {
                        player.ChatMessage("Currently jailed players:");
                        foreach (string info in jailedPlayersInfo)
                        {
                            player.ChatMessage(info);
                        }
                    }
                    return;

                default:
                    break;
            }
        }

        [ConsoleCommand("jail")]
        private void ccJailCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
                return;

            if (arg.Args.Length < 1)
            {
                SendReply(arg, "[<color=#FF4F4B>Jail</color>]\n");
                SendReply(arg, "jail send <playername/id> [minutes:optional] [reason:optional] - Send to Jail\n");
                SendReply(arg, "jail free <playername/id> - Release from Jail\n");
                SendReply(arg, "jail extend <playername/id> [minutes:optional] - Extend Jail Time\n");
                return;
            }

            switch (arg.Args[0].ToLower())
            {
                case "send":
                    if (arg.Args.Length >= 2)
                    {
                        BasePlayer targetPlayer = FindPlayersSingleNull(arg.Args[1]);
                        bool offline = false;
                        ulong targetId = 0;
                        if (targetPlayer == null || !targetPlayer.IsConnected)
                        {

                            if (targetPlayer.userID.IsSteamId() && !targetPlayer.IsConnected)
                            {
                                if (storedData.jailedPlayers.Any(jp => jp.playerId == targetId))
                                {
                                     SendReply(arg, "Player is already jailed.");
                                    return;
                                }

                                offline = true;
                            }
                            else
                            {
                                 SendReply(arg, "Player not found.");
                                return;
                            }
                        }

                        if (storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            SendReply(arg, "Player is already jailed, use jail extend to extend time.");
                            return;
                        }

                        TimeSpan jailTime = TimeSpan.FromMinutes(configData.defaultJailTime);
                        string reason = "No reason provided";
                        if (arg.Args.Length >= 2 && int.TryParse(arg.Args[2], out int minutes))
                        {
                            jailTime = TimeSpan.FromMinutes(minutes);
                        }
                        if (arg.Args.Length >= 3)
                        {
                            reason = string.Join(" ", arg.Args, 3, arg.Args.Length - 2);
                        }

                        JailPlayer(targetPlayer, jailTime, reason);
                        SendReply(arg, $"[<color=#FF4F4B>Jail</color>] You have sent {targetPlayer.displayName} to jail for {arg.Args[2]} minutes because {reason}");
                        PrintToChat($"[<color=#FF4F4B>Jail</color>] Player {targetPlayer.displayName} was sent to jail for {arg.Args[2]} minutes because {reason}");
                        Puts($"Player {targetPlayer.displayName} was sent to jail for {arg.Args[2]} minutes because {reason} by Console");
                    }
                    return;

                case "free":
                    if (arg.Args.Length >= 2)
                    {
                        BasePlayer targetPlayer = BasePlayer.Find(arg.Args[1]); ;
                        if (targetPlayer == null)
                        {
                            SendReply(arg, "Player not found.");
                            return;
                        }
                        if (!storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            SendReply(arg, "Player is not jailed.");
                            return;
                        }

                        ReleasePlayer(targetPlayer.userID);
                        SendReply(arg, $"[<color=#FF4F4B>Jail</color>] You have released {targetPlayer.displayName} from jail.");
                        Puts($"Player {targetPlayer.displayName} was released from jail by Console");
                    }
                    return;

                case "extend":
                    if (arg.Args.Length >= 2)
                    {
                        int extraMinutes = int.MaxValue;
                        if (arg.Args.Length > 2)
                        {
                            extraMinutes = int.Parse(arg.Args[2]);
                        }

                        BasePlayer targetPlayer = BasePlayer.Find(arg.Args[1]);
                        if (targetPlayer == null)
                        {
                            SendReply(arg, "Player not found.");
                            return;
                        }
                        if (!storedData.jailedPlayers.Any(jp => jp.playerId == targetPlayer.userID))
                        {
                            SendReply(arg, "Player is not jailed.");
                            return;
                        }

                        ExtendJailTime(targetPlayer.userID, TimeSpan.FromMinutes(extraMinutes));
                        SendReply(arg, $"[<color=#FF4F4B>Jail</color>] You have extended the jail time for {targetPlayer.displayName} by {extraMinutes} minutes.");
                        Puts($"Player {targetPlayer.displayName} was extended jail time by {extraMinutes} minutes by Console");
                    }
                    return;

                case "status":
                    List<string> jailedPlayersInfo = new List<string>();

                    foreach (var kvp in storedData.jailedPlayers)
                    {
                        BasePlayer jailedPlayer = BasePlayer.FindByID(kvp.playerId);
                        if (jailedPlayer != null && jailedPlayer.IsConnected)
                        {
                            TimeSpan remainingTime = kvp.releaseTime - DateTime.UtcNow;
                            string formattedTime = $"{remainingTime.Days}{remainingTime.Hours:D2}:{remainingTime.Minutes:D2}:{remainingTime.Seconds:D2}";

                            jailedPlayersInfo.Add($"<color=orange>{jailedPlayer.displayName}</color> - {formattedTime} remaining, Reason: {kvp.jailReason}");
                        }
                    }

                    if (jailedPlayersInfo.Count == 0)
                    {
                        SendReply(arg, "There are no players currently jailed online.");
                    }
                    else
                    {
                        SendReply(arg, "Currently jailed players:");
                        foreach (string info in jailedPlayersInfo)
                        {
                            SendReply(arg, info);
                        }
                    }
                    return;

                default:
                    break;
            }
        }

        #endregion Commands

        #region Utility Methods

        private BasePlayer FindPlayersSingle(string value, BasePlayer player) => (BasePlayer)NTeleportation.Call("API_FindPlayer", value, player);
        private BasePlayer FindPlayersSingleNull(string value) => (BasePlayer)NTeleportation.Call("API_FindPlayerNull", value);

        public void StartSleeping(BasePlayer player) // custom as to not cancel crafting, or remove player from vanish
        {
            if (!player.IsSleeping())
            {
                Interface.CallHook("OnPlayerSleep", player);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, b: true);
                player.sleepStartTime = UnityEngine.Time.time;
                BasePlayer.sleepingPlayerList.Add(player);
                player.CancelInvoke("InventoryUpdate");
                player.CancelInvoke("TeamUpdate");
                player.inventory.loot.Clear();
                player.inventory.containerMain.OnChanged();
                player.inventory.containerBelt.OnChanged();
                player.inventory.containerWear.OnChanged();
                player.Invoke("TurnOffAllLights", 0f);
                if (!player._limitedNetworking)
                {
                    player.EnablePlayerCollider();
                    player.RemovePlayerRigidbody();
                }
                else player.RemoveFromTriggers();
                player.SetServerFall(wantsOn: true);
            }
        }

        private void EndSleeping(BasePlayer player)
        {
            if (player.IsSleeping())
            {
                if (player.IsRestrained)
                {
                    player.inventory.SetLockedByRestraint(flag: true);
                }
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, b: false);
                player.sleepStartTime = -1f;
                BasePlayer.sleepingPlayerList.Remove(player);
                player.CancelInvoke(player.ScheduledDeath);
                player.InvokeRepeating("InventoryUpdate", 1f, 0.1f * UnityEngine.Random.Range(0.99f, 1.01f));
                if (RelationshipManager.TeamsEnabled())
                {
                    player.InvokeRandomized(player.TeamUpdate, 1f, 4f, 1f);
                }
                player.InvokeRandomized(player.UpdateClanLastSeen, 300f, 300f, 60f);
                player.inventory.containerMain.OnChanged();
                player.inventory.containerBelt.OnChanged();
                player.inventory.containerWear.OnChanged();
                Interface.CallHook("OnPlayerSleepEnded", this);
                EACServer.LogPlayerSpawn(player);
                if (player.State?.pings?.Count > 0)
                {
                    player.SendPingsToClient();
                }
                if (TutorialIsland.ShouldPlayerBeAskedToStartTutorial(player))
                {
                    player.ClientRPC(RpcTarget.Player("PromptToStartTutorial", player));
                }
            }
        }

        private void SendStaffMessage(string name, string msg)
        {
            foreach (BasePlayer current in BasePlayer.activePlayerList)
            {
                if (permission.UserHasPermission(current.userID.Get().ToString(), AdminPermission))
                {
                    SendReply(current, $"[Jailed Inmate Chat] <color=orange>{name}</color>: {msg}");
                }
            }
        }

        private object GetSpawnLocation()
        {
            object success = Spawns.Call("GetRandomSpawn", new object[] { "jail" });
            if (success == null || success is string)
            {
                PrintError("There was an error retrieving spawn location.");
                return null;
            }
            return (Vector3)success;
        }

        public object ValidateSpawnFile(string name)
        {
            object success = Spawns?.Call("GetSpawnsCount", name);
            if (success is string)
                return false;
            else return null;
        }

        private T GetConfig<T>(string key, T defaultValue)
        {
            return Config[key] == null ? defaultValue : (T)Convert.ChangeType(Config[key], typeof(T));
        }

        private bool CanWake(BasePlayer player)
        {
            return player.IsOnGround() || player.limitNetworking || player.IsFlying || player.IsAdmin;
        }

        #endregion Utility Methods

        #region Discord

        private string LookupIP(BasePlayer player)
        {
            if (player.Connection.ipaddress == null || player.Connection.ipaddress == "" || !player.IsConnected)
                return "N/A";
            else
            {
                if (!player.Connection.ipaddress.Contains(":"))
                    return player.Connection.ipaddress;
                return player.Connection.ipaddress.Substring(0, player.Connection.ipaddress.LastIndexOf(":"));
            }
        }

        private string LookupDiscord(BasePlayer player)
        {
            if (player.IsConnected)
            {
                /*if (_link.IsLinked(player.IPlayer))
                {
                    return $"<@{_link.GetDiscordId(player.IPlayer)}>";
                }*/
                return "Not Linked";
            }

            return "Not Linked";
        }

        private void SendToDiscordBot(BasePlayer player, BasePlayer jailer, string reason, string time)
        {
            string ipaddress = LookupIP(player);
            string discordId = LookupDiscord(player);

            var embed = new WebHookEmbed
            {
                Fields = new List<WebHookField>
            {
                new WebHookField
                {
                    Name = "Server: ",
                    Value =  $"```{ConVar.Admin.ServerInfo().Hostname.ToString()}```",
                    Inline = false
                },
                new WebHookField
                {
                    Name = "Staff Member: ",
                    Value = $"```{jailer.ToString()}```",
                    Inline = false
                },
                new WebHookField
                {
                    Name = "Jailed Player: ",
                    Value =  $"```{player.displayName}```",
                    Inline = false
                },
                new WebHookField
                {
                    Name = "Jailed Player Steam ID: ",
                    Value =  $"```{player.UserIDString}```",
                    Inline = true
                },
                new WebHookField
                {
                    Name = "Jailed Player IP: ",
                    Value =  $"```{ipaddress}```",
                    Inline = false
                },
                new WebHookField
                {
                    Name = "Jailed Player Discord: ",
                    Value =  $"```{discordId}```",
                    Inline = true
                },
                new WebHookField
                {
                    Name = "Jailed Time: ",
                    Value =  $"```{time} minutes```",
                    Inline = true
                },
                new WebHookField
                {
                    Name = "Reason: ",
                    Value =  $"```{reason}```",
                    Inline = false
                }
            },
                Footer = new WebHookFooter { Text = $"Logged {DateTime.Now}" }
            };

            var embedBody = new WebHookEmbedBody
            {
                Embeds = new[]
                {
                embed
            }
            };

            webrequest.Enqueue(configData.webhook, JsonConvert.SerializeObject(embedBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }),
            (code, result) => { }, this, RequestMethod.POST,
            new Dictionary<string, string> { { "Content-Type", "application/json" } });
        }

        private class WebHookEmbedBody
        {
            [JsonProperty(PropertyName = "embeds")]
            public WebHookEmbed[] Embeds;
        }

        private class WebHookContentBody
        {
        }

        private class WebHookEmbed
        {
            [JsonProperty(PropertyName = "title")]
            public string Title;

            [JsonProperty(PropertyName = "type")]
            public string Type = "rich";

            [JsonProperty(PropertyName = "description")]
            public string Description;

            [JsonProperty(PropertyName = "color")]
            public int Color;

            [JsonProperty(PropertyName = "author")]
            public WebHookAuthor Author;

            [JsonProperty(PropertyName = "image")]
            public WebHookImage Image;

            [JsonProperty(PropertyName = "fields")]
            public List<WebHookField> Fields;

            [JsonProperty(PropertyName = "footer")]
            public WebHookFooter Footer;
        }

        private class WebHookAuthor
        {
            [JsonProperty(PropertyName = "name")]
            public string Name;

            [JsonProperty(PropertyName = "url")]
            public string AuthorUrl;

            [JsonProperty(PropertyName = "icon_url")]
            public string AuthorIconUrl;
        }

        private class WebHookImage
        {
            [JsonProperty(PropertyName = "proxy_url")]
            public string ProxyUrl;

            [JsonProperty(PropertyName = "url")]
            public string Url;

            [JsonProperty(PropertyName = "height")]
            public int? Height;

            [JsonProperty(PropertyName = "width")]
            public int? Width;
        }

        private class WebHookField
        {
            [JsonProperty(PropertyName = "name")]
            public string Name;

            [JsonProperty(PropertyName = "value")]
            public string Value;

            [JsonProperty(PropertyName = "inline")]
            public bool Inline;
        }

        private class WebHookFooter
        {
            [JsonProperty(PropertyName = "text")]
            public string Text;

            [JsonProperty(PropertyName = "icon_url")]
            public string IconUrl;

            [JsonProperty(PropertyName = "proxy_icon_url")]
            public string ProxyIconUrl;
        }

        #endregion Discord
    }
}