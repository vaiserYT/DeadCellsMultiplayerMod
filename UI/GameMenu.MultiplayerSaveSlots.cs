using dc.hxd;
using dc.hl.types;
using dc.pr;
using dc.tool;
using dc.ui;
using HaxeProxy.Runtime;
using DeadCellsMultiplayerMod.MultiplayerModUI.Connection;
using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;
using Marshal = System.Runtime.InteropServices.Marshal;

namespace DeadCellsMultiplayerMod
{
    internal static partial class GameMenu
    {
        private const string MultiplayerSaveFolderName = "MSave";
        private const string SavedGamesTitleLocalizationKey = "SAUVEGARDES";
        private const int CopyActionCode = 20;
        private const int LiteralKeyboardXKeyCode = 88;

        private enum MultiplayerSaveMenuKind
        {
            None,
            MultiplayerSlots,
            OriginalSourceSelection
        }

        private static bool _multiplayerSaveHooksAttached;
        private static bool _multiplayerSaveMenuOpening;
        private static MultiplayerSaveMenuKind _multiplayerSaveMenuKind = MultiplayerSaveMenuKind.None;
        private static NetRole _multiplayerSaveMenuReturnRole = NetRole.None;
        private static int? _multiplayerSaveImportTargetSlot;
        private static int? _preferredMultiplayerSaveSlot;
        private static bool _forceMultiplayerSaveStore;
        private static ControlLabel? _multiplayerSaveImportControlLabel;
        private static string _multiplayerSaveDefaultTitle = string.Empty;
        private static bool _hasCapturedMultiplayerSaveDefaultTitle;
        private static bool _pendingSaveChoiceReflow;
        private static MultiplayerSaveMenuKind _pendingSaveChoiceReflowKind = MultiplayerSaveMenuKind.None;

        private static void InitializeMultiplayerSaveHooks()
        {
            if (_multiplayerSaveHooksAttached)
                return;

            Hook__Save.fileName += Hook__Save_fileName;
            Hook_TitleScreen.onLeavingSaveMenu += Hook_TitleScreen_onLeavingSaveMenu;
            Hook__SaveChoice.__constructor__ += Hook__SaveChoice___constructor__;
            Hook_SaveChoice.onCopy += Hook_SaveChoice_onCopy;
            Hook_SaveChoice.onValidate += Hook_SaveChoice_onValidate;
            Hook_SaveChoice.onCancel += Hook_SaveChoice_onCancel;
            Hook_SaveChoice.onDelete += Hook_SaveChoice_onDelete;
            Hook_SaveChoice.onDispose += Hook_SaveChoice_onDispose;
            Hook_SaveChoice.update += Hook_SaveChoice_update;

            _multiplayerSaveHooksAttached = true;
        }

        private static string GetMultiplayerSaveButtonLabel()
        {
            return FormatLocalized("Save: Slot {0}", ResolveSaveSlotNumber(null) + 1);
        }

        private static void OpenMultiplayerSlotMenu(TitleScreen screen)
        {
            _multiplayerSaveMenuReturnRole = _inHostStatusMenu
                ? NetRole.Host
                : _inClientWaitingMenu
                    ? NetRole.Client
                    : _role;

            OpenSaveMenu(screen, MultiplayerSaveMenuKind.MultiplayerSlots);
        }

        private static void OpenOriginalSaveImportMenu(TitleScreen screen)
        {
            OpenSaveMenu(screen, MultiplayerSaveMenuKind.OriginalSourceSelection);
        }

        private static void OpenSaveMenu(TitleScreen screen, MultiplayerSaveMenuKind kind)
        {
            _multiplayerSaveMenuKind = kind;
            _multiplayerSaveMenuOpening = true;
            _multiplayerSaveImportControlLabel = null;

            try
            {
                screen.saveMenu();
                screen.ShouldAutoHideConnectionUI(false);
            }
            catch (Exception ex)
            {
                _multiplayerSaveMenuKind = MultiplayerSaveMenuKind.None;
                _log?.Warning("[NetMod] Failed to open save menu {Kind}: {Message}", kind, ex.Message);
            }
            finally
            {
                _multiplayerSaveMenuOpening = false;
            }
        }

        private static void Hook_TitleScreen_onLeavingSaveMenu(Hook_TitleScreen.orig_onLeavingSaveMenu orig, TitleScreen self)
        {
            var returnRole = _multiplayerSaveMenuReturnRole;

            _multiplayerSaveMenuKind = MultiplayerSaveMenuKind.None;
            _multiplayerSaveMenuOpening = false;
            _multiplayerSaveImportTargetSlot = null;
            _multiplayerSaveImportControlLabel = null;
            _pendingSaveChoiceReflow = false;
            _pendingSaveChoiceReflowKind = MultiplayerSaveMenuKind.None;

            orig(self);

            _multiplayerSaveMenuReturnRole = NetRole.None;

            if (returnRole == NetRole.Host)
            {
                ShowHostStatusMenu(self);
                self.ShouldAutoHideConnectionUI(true);
            }
            else if (returnRole == NetRole.Client)
            {
                ShowClientWaitingMenu(self);
                self.ShouldAutoHideConnectionUI(true);
            }
        }

        private static void Hook__SaveChoice___constructor__(Hook__SaveChoice.orig___constructor__ orig, SaveChoice self, TitleScreen tween)
        {
            orig(self, tween);

            try
            {
                AttachSaveChoiceActionBridge(self);
                TryCaptureDefaultSaveTitle(self);

                switch (_multiplayerSaveMenuKind)
                {
                    case MultiplayerSaveMenuKind.MultiplayerSlots:
                        ConfigureMultiplayerSaveChoice(self);
                        break;
                    case MultiplayerSaveMenuKind.OriginalSourceSelection:
                        ConfigureOriginalSourceSaveChoice(self);
                        break;
                }
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to customize save choice UI: {Message}", ex.Message);
            }
        }

        private static void Hook_SaveChoice_onCopy(Hook_SaveChoice.orig_onCopy orig, SaveChoice self)
        {
            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.OriginalSourceSelection)
                return;

            if (_multiplayerSaveMenuKind != MultiplayerSaveMenuKind.MultiplayerSlots)
            {
                orig(self);
                return;
            }

            if (!TryBeginMultiplayerSaveImportSelection(self))
                return;
        }

        private static void Hook_SaveChoice_onValidate(Hook_SaveChoice.orig_onValidate orig, SaveChoice self)
        {
            if (_multiplayerSaveMenuKind != MultiplayerSaveMenuKind.OriginalSourceSelection)
            {
                int? selectedMultiplayerSlot = null;
                if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.MultiplayerSlots &&
                    TryGetSelectedSaveSlot(self, out var selectedSlot))
                {
                    _preferredMultiplayerSaveSlot = selectedSlot;
                    selectedMultiplayerSlot = selectedSlot;
                }

                orig(self);
                if (selectedMultiplayerSlot.HasValue)
                {
                    SetCurrentSaveSlot(selectedMultiplayerSlot.Value);
                    NotifyMultiplayerSaveSlotChanged();
                }
                return;
            }

            if (!TryGetSelectedSourceSaveSlot(self, out var sourceSlot))
                return;
            if (!_multiplayerSaveImportTargetSlot.HasValue)
                return;
            if (!CopyOriginalSaveIntoMultiplayerSlot(sourceSlot, _multiplayerSaveImportTargetSlot.Value))
                return;

            _preferredMultiplayerSaveSlot = _multiplayerSaveImportTargetSlot.Value;
            SetCurrentSaveSlot(_multiplayerSaveImportTargetSlot.Value);
            NotifyMultiplayerSaveSlotChanged();
            _multiplayerSaveImportTargetSlot = null;
            SwitchSaveChoiceStore(self, MultiplayerSaveMenuKind.MultiplayerSlots);
        }

        private static void Hook_SaveChoice_onCancel(Hook_SaveChoice.orig_onCancel orig, SaveChoice self)
        {
            if (_multiplayerSaveMenuKind != MultiplayerSaveMenuKind.OriginalSourceSelection)
            {
                orig(self);
                return;
            }

            _multiplayerSaveImportTargetSlot = null;
            SwitchSaveChoiceStore(self, MultiplayerSaveMenuKind.MultiplayerSlots);
        }

        private static void Hook_SaveChoice_onDelete(Hook_SaveChoice.orig_onDelete orig, SaveChoice self)
        {
            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.OriginalSourceSelection)
                return;

            int? deletedMultiplayerSlot = null;
            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.MultiplayerSlots &&
                TryGetSelectedSaveSlot(self, out var selectedSlot))
            {
                deletedMultiplayerSlot = selectedSlot;
            }

            orig(self);

            if (deletedMultiplayerSlot.HasValue)
            {
                MUser.ClearCoopId(deletedMultiplayerSlot.Value);
                NotifyMultiplayerSaveSlotChanged();
            }
        }

        private static void Hook_SaveChoice_onDispose(Hook_SaveChoice.orig_onDispose orig, SaveChoice self)
        {
            try
            {
                orig(self);
            }
            finally
            {
                _multiplayerSaveMenuOpening = false;
                _multiplayerSaveImportControlLabel = null;
                _pendingSaveChoiceReflow = false;
                _pendingSaveChoiceReflowKind = MultiplayerSaveMenuKind.None;
            }
        }

        private static void Hook_SaveChoice_update(Hook_SaveChoice.orig_update orig, SaveChoice self)
        {
            TryFlushPendingSaveChoiceReflow(self);
            EnsureCurrentSaveChoiceTitle(self);

            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.MultiplayerSlots)
            {
                var copyActionPressed = IsActionPressed(self?.controller, CopyActionCode);
                var literalXPressed = IsLiteralXPressed();

                if (self != null &&
                    (copyActionPressed || literalXPressed) &&
                    TryBeginMultiplayerSaveImportSelection(self))
                {
                    return;
                }
            }

            orig(self);
        }

        private static dc.String Hook__Save_fileName(Hook__Save.orig_fileName orig, int? slot)
        {
            if (!ShouldUseMultiplayerSaveStore())
                return orig(slot);

            try
            {
                EnsureMultiplayerSaveFolderExists();
                return MakeHLString(GetMultiplayerSaveRelativeFilePath(slot));
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to resolve multiplayer save path: {Message}", ex.Message);
                return orig(slot);
            }
        }

        private static bool ShouldUseMultiplayerSaveStore()
        {
            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.OriginalSourceSelection)
                return false;

            return _forceMultiplayerSaveStore || _role != NetRole.None || _multiplayerSaveMenuKind == MultiplayerSaveMenuKind.MultiplayerSlots || _multiplayerSaveMenuOpening;
        }

        private static int ResolveSaveSlotNumber(int? slot)
        {
            if (slot.HasValue && slot.Value >= 0)
                return slot.Value;

            try
            {
                var current = dc.Main.Class.ME?.options?.curSlot;
                if (current.HasValue && current.Value >= 0)
                    return current.Value;
            }
            catch
            {
            }

            return 0;
        }

        private static string GetSaveRootPath()
        {
            try
            {
                var saveRoot = dc.tool.File.Class.PATH?.ToString();
                if (!string.IsNullOrWhiteSpace(saveRoot))
                    return IOPath.GetFullPath(saveRoot);
            }
            catch
            {
            }

            try
            {
                return IOPath.GetFullPath("save");
            }
            catch
            {
                return IOPath.Combine(Environment.CurrentDirectory, "save");
            }
        }

        private static string GetOriginalSaveRelativeFilePath(int? slot)
        {
            return $"user_{ResolveSaveSlotNumber(slot)}.dat";
        }

        private static string GetMultiplayerSaveRelativeFilePath(int? slot)
        {
            return $"{MultiplayerSaveFolderName}/user_{ResolveSaveSlotNumber(slot)}.dat";
        }

        private static string GetAbsoluteSavePath(string relativePath)
        {
            var normalized = relativePath
                .Replace('/', IOPath.DirectorySeparatorChar)
                .Replace('\\', IOPath.DirectorySeparatorChar);

            return IOPath.GetFullPath(IOPath.Combine(GetSaveRootPath(), normalized));
        }

        private static void EnsureMultiplayerSaveFolderExists()
        {
            IODirectory.CreateDirectory(GetAbsoluteSavePath(MultiplayerSaveFolderName));
        }

        private static void ConfigureMultiplayerSaveChoice(SaveChoice self)
        {
            if (self == null)
                return;
            if (self.title != null)
                self.title.set_text(MakeHLString(ResolveSavedGamesTitle()));

            SetControlLabelVisible(self, 0, true);
            SetControlLabelVisible(self, 1, false);
            EnsureImportControlLabel(self);
            TrySelectPreferredMultiplayerSlot(self);
            self.fControlLabel?.reflow();
        }

        private static void ConfigureOriginalSourceSaveChoice(SaveChoice self)
        {
            if (self == null)
                return;
            if (self.title != null)
                self.title.set_text(MakeHLString(Localize("Choose original save to copy")));

            SetControlLabelVisible(self, 0, false);
            SetControlLabelVisible(self, 1, false);
            self.fControlLabel?.reflow();
        }

        private static void SwitchSaveChoiceStore(SaveChoice self, MultiplayerSaveMenuKind kind)
        {
            if (self == null)
                return;

            _multiplayerSaveMenuKind = kind;
            _pendingSaveChoiceReflow = true;
            _pendingSaveChoiceReflowKind = kind;
            TryRebuildSaveChoice(self, kind);

            try
            {
                switch (kind)
                {
                    case MultiplayerSaveMenuKind.MultiplayerSlots:
                        ConfigureMultiplayerSaveChoice(self);
                        break;
                    case MultiplayerSaveMenuKind.OriginalSourceSelection:
                        ConfigureOriginalSourceSaveChoice(self);
                        break;
                }

                EnsureValidSaveChoiceSelection(self);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to switch save choice store to {Kind}: {Message}", kind, ex.Message);
            }
        }

        private static void TryFlushPendingSaveChoiceReflow(SaveChoice self)
        {
            if (!_pendingSaveChoiceReflow || self == null)
                return;
            if (_pendingSaveChoiceReflowKind != MultiplayerSaveMenuKind.None &&
                _pendingSaveChoiceReflowKind != _multiplayerSaveMenuKind)
            {
                _pendingSaveChoiceReflow = false;
                _pendingSaveChoiceReflowKind = MultiplayerSaveMenuKind.None;
                return;
            }

            TryRebuildSaveChoice(self, _multiplayerSaveMenuKind);
        }

        private static void TryRebuildSaveChoice(SaveChoice self, MultiplayerSaveMenuKind kind)
        {
            try
            {
                self.fSave?.reflow();
                _pendingSaveChoiceReflow = false;
                _pendingSaveChoiceReflowKind = MultiplayerSaveMenuKind.None;
            }
            catch (Exception ex)
            {
                if (!IsBenignSaveRebuildException(ex))
                    _log?.Warning("[NetMod] Failed to rebuild save choice for {Kind}: {Message}", kind, ex.Message);
            }
        }

        private static void AttachSaveChoiceActionBridge(SaveChoice self)
        {
            var controller = self?.controller;
            if (controller == null)
                return;

            var previousOnActPressed = controller.onActPressed;
            controller.onActPressed = new HlAction<int, bool>((act, isKey) =>
            {
                previousOnActPressed?.Invoke(act, isKey);
                if (self != null)
                    HandleSaveChoiceActionPressed(self, act);
            });
        }

        private static void HandleSaveChoiceActionPressed(SaveChoice self, int act)
        {
            if (act != CopyActionCode)
                return;
            if (_multiplayerSaveMenuKind != MultiplayerSaveMenuKind.MultiplayerSlots)
                return;

            try
            {
                TryBeginMultiplayerSaveImportSelection(self);
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to handle multiplayer save import action: {Message}", ex.Message);
            }
        }

        private static bool TryBeginMultiplayerSaveImportSelection(SaveChoice self)
        {
            if (_multiplayerSaveMenuKind != MultiplayerSaveMenuKind.MultiplayerSlots)
                return false;
            if (!TryResolveImportTargetSlot(self, out var targetSlot))
                targetSlot = ResolveSaveSlotNumber(null);
            if (targetSlot < 0)
                targetSlot = 0;

            _multiplayerSaveImportTargetSlot = targetSlot;
            _preferredMultiplayerSaveSlot = targetSlot;
            SwitchSaveChoiceStore(self, MultiplayerSaveMenuKind.OriginalSourceSelection);
            return true;
        }

        private static bool TryResolveImportTargetSlot(SaveChoice self, out int slot)
        {
            slot = 0;
            if (TryGetSelectedSaveSlot(self, out slot))
                return true;

            var curSaveId = self?.curSaveId;
            if (curSaveId.HasValue && curSaveId.Value >= 0)
            {
                slot = curSaveId.Value;
                return true;
            }

            try
            {
                var current = dc.Main.Class.ME?.options?.curSlot;
                if (current.HasValue && current.Value >= 0)
                {
                    slot = current.Value;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void TryCaptureDefaultSaveTitle(SaveChoice self)
        {
            if (_hasCapturedMultiplayerSaveDefaultTitle)
                return;
            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.OriginalSourceSelection)
                return;

            var title = self?.title?.rawText?.ToString();
            if (string.IsNullOrWhiteSpace(title))
                return;
            if (string.Equals(title, Localize("Choose original save to copy"), StringComparison.OrdinalIgnoreCase))
                return;

            _multiplayerSaveDefaultTitle = title;
            _hasCapturedMultiplayerSaveDefaultTitle = true;
        }

        private static string ResolveSavedGamesTitle()
        {
            try
            {
                var localized = dc.Lang.Class.t?.get(MakeHLString(SavedGamesTitleLocalizationKey), null)?.ToString();
                if (!string.IsNullOrWhiteSpace(localized))
                {
                    _multiplayerSaveDefaultTitle = localized;
                    _hasCapturedMultiplayerSaveDefaultTitle = true;
                    return localized;
                }
            }
            catch
            {
            }

            return string.IsNullOrWhiteSpace(_multiplayerSaveDefaultTitle)
                ? Localize("Saved games")
                : _multiplayerSaveDefaultTitle;
        }

        private static void EnsureCurrentSaveChoiceTitle(SaveChoice self)
        {
            var title = self?.title;
            if (title == null)
                return;

            try
            {
                switch (_multiplayerSaveMenuKind)
                {
                    case MultiplayerSaveMenuKind.OriginalSourceSelection:
                        if (!string.Equals(title.rawText?.ToString(), Localize("Choose original save to copy"), StringComparison.Ordinal))
                            title.set_text(MakeHLString(Localize("Choose original save to copy")));
                        break;
                    case MultiplayerSaveMenuKind.MultiplayerSlots:
                        if (string.Equals(title.rawText?.ToString(), Localize("Choose original save to copy"), StringComparison.OrdinalIgnoreCase))
                            title.set_text(MakeHLString(ResolveSavedGamesTitle()));
                        break;
                }
            }
            catch
            {
            }
        }

        private static bool IsBenignSaveRebuildException(Exception ex)
        {
            var message = ex?.Message;
            return !string.IsNullOrEmpty(message) &&
                   message.IndexOf("Null access ._getCdObject", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsActionPressed(ControllerAccess? controllerAccess, int actionCode)
        {
            if (controllerAccess == null)
                return false;
            if (controllerAccess.manualLock)
                return false;

            var controller = controllerAccess.parent;
            if (controller == null)
                return false;
            if (controller.isLocked)
                return false;
            if (controller.exclusiveId != null && controller.exclusiveId != controllerAccess.id)
                return false;
            if (!(GetCurrentUnixTimeSeconds() >= controller.suspendTimer))
                return false;

            return IsPressed(controller, controller.get_bindings().padA, actionCode, isGamepad: true) ||
                   IsPressed(controller, controller.get_bindings().padB, actionCode, isGamepad: true) ||
                   IsPressed(controller, controller.get_bindings().padC, actionCode, isGamepad: true) ||
                   IsPressed(controller, controller.get_bindings().primary, actionCode, isGamepad: false) ||
                   IsPressed(controller, controller.get_bindings().secondary, actionCode, isGamepad: false) ||
                   IsPressed(controller, controller.get_bindings().third, actionCode, isGamepad: false);
        }

        private static bool IsLiteralXPressed()
        {
            return Key.Class.isPressed.Invoke(LiteralKeyboardXKeyCode);
        }

        private static bool IsPressed(Controller controller, ArrayBytes_Int? bindings, int actionCode, bool isGamepad)
        {
            var keyCode = GetBinding(bindings, actionCode);
            if (keyCode < 0)
                return false;

            if (isGamepad)
                return controller.padIsPressed(keyCode);

            return (controller.mode & Controller.Class.ENABLE_KEY) != 0 && Key.Class.isPressed.Invoke(keyCode);
        }

        private static int GetBinding(ArrayBytes_Int? bindings, int actionCode)
        {
            if (bindings == null)
                return -1;
            if ((uint)actionCode >= (uint)bindings.length)
                return 0;

            return Marshal.ReadInt32(bindings.bytes, actionCode << 2);
        }

        private static double GetCurrentUnixTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        }

        private static void EnsureValidSaveChoiceSelection(SaveChoice self)
        {
            if (self == null)
                return;
            var saves = self.saves;
            if (saves == null || saves.length <= 0)
                return;

            if (_multiplayerSaveMenuKind == MultiplayerSaveMenuKind.MultiplayerSlots)
            {
                TrySelectPreferredMultiplayerSlot(self);
                return;
            }

            var targetIndex = self.curSaveId;
            if (targetIndex < 0)
                targetIndex = 0;
            if (targetIndex >= saves.length)
                targetIndex = saves.length - 1;

            try
            {
                var instant = true;
                self.select(targetIndex, Ref<bool>.From(ref instant));
            }
            catch
            {
                self.curSaveId = targetIndex;
                self.moveSelection();
                self.fControlLabel?.reflow();
            }
        }

        private static void EnsureImportControlLabel(SaveChoice self)
        {
            if (self?.fControlLabel == null)
                return;

            var existing = _multiplayerSaveImportControlLabel;
            if (existing == null || existing.parent != self.fControlLabel)
                existing = FindImportControlLabel(self);

            if (existing != null)
            {
                _multiplayerSaveImportControlLabel = existing;
                RemoveDuplicateImportControlLabels(self, existing);
                existing.set_visible(true);
                existing.tfLabel?.set_text(MakeHLString(Localize("Copy your save in that slot")));
                existing.reflow();
                self.fControlLabel.reflow();
                return;
            }

            var importLabel = new ControlLabel(CreateActionArray(CopyActionCode), MakeHLString(Localize("Copy your save in that slot")), null, null, null, null);
            importLabel.set_visible(true);
            importLabel.reflow();
            _multiplayerSaveImportControlLabel = importLabel;
            self.fControlLabel.addChild(importLabel);
            self.fControlLabel.reflow();
        }

        private static ControlLabel? FindImportControlLabel(SaveChoice self)
        {
            var children = self?.fControlLabel?.children;
            if (children == null)
                return null;

            for (var i = 0; i < children.length; i++)
            {
                if (children.array[i] is not ControlLabel label)
                    continue;

                var rawText = label.tfLabel?.rawText?.ToString();
                if (string.Equals(rawText, Localize("Copy your save in that slot"), StringComparison.Ordinal))
                    return label;
            }

            return null;
        }

        private static void RemoveDuplicateImportControlLabels(SaveChoice self, ControlLabel keep)
        {
            var controlParent = self?.fControlLabel;
            var children = controlParent?.children;
            if (children == null || controlParent == null)
                return;

            for (var i = children.length - 1; i >= 0; i--)
            {
                if (children.array[i] is not ControlLabel label || ReferenceEquals(label, keep))
                    continue;

                var rawText = label.tfLabel?.rawText?.ToString();
                if (!string.Equals(rawText, Localize("Copy your save in that slot"), StringComparison.Ordinal))
                    continue;

                controlParent.removeChild(label);
            }
        }

        private static void SetControlLabelVisible(SaveChoice self, int index, bool visible)
        {
            var controlLabel = GetControlLabel(self, index);
            if (controlLabel == null)
                return;

            controlLabel.set_visible(visible);
            controlLabel.reflow();
        }

        private static ControlLabel? GetControlLabel(SaveChoice self, int index)
        {
            var children = self?.fControlLabel?.children;
            if (children == null || index < 0 || index >= children.length)
                return null;

            return children.array[index] as ControlLabel;
        }

        private static ArrayBytes_Int CreateActionArray(int actionCode)
        {
            var values = new ArrayBytes_Int();
            try
            {
                values.push(actionCode);
            }
            catch
            {
                values.pushDyn(actionCode);
            }

            return values;
        }

        private static void TrySelectPreferredMultiplayerSlot(SaveChoice self)
        {
            if (self == null || !_preferredMultiplayerSaveSlot.HasValue)
                return;

            var targetSlot = _preferredMultiplayerSaveSlot.Value;
            var saves = self.saves;
            if (saves == null)
                return;

            if (targetSlot >= 0 && targetSlot < saves.length)
            {
                try
                {
                    var instant = true;
                    self.select(targetSlot, Ref<bool>.From(ref instant));
                }
                catch
                {
                    self.curSaveId = targetSlot;
                    self.moveSelection();
                    self.fControlLabel?.reflow();
                }

                return;
            }

            for (var i = 0; i < saves.length; i++)
            {
                if (saves.array[i] is not SaveWindow window || window.si == null || window.si.index != targetSlot)
                    continue;

                try
                {
                    var instant = true;
                    self.select(i, Ref<bool>.From(ref instant));
                }
                catch
                {
                    self.curSaveId = i;
                }

                return;
            }
        }

        private static bool TryGetSelectedSaveWindow(SaveChoice self, out SaveWindow? window)
        {
            window = null;
            if (self == null)
                return false;
            var saves = self.saves;
            if (saves == null)
                return false;

            var selectedIndex = self.curSaveId;
            if (selectedIndex < 0 || selectedIndex >= saves.length)
                return false;

            window = saves.array[selectedIndex] as SaveWindow;
            return window != null;
        }

        private static bool TryGetSelectedSaveSlot(SaveChoice self, out int slot)
        {
            slot = 0;
            if (TryGetSelectedSaveWindow(self, out var window) && window?.si != null)
            {
                slot = window.si.index;
                return slot >= 0;
            }

            return TryGetSelectedSaveIndex(self, out slot);
        }

        private static bool TryGetSelectedSourceSaveSlot(SaveChoice self, out int slot)
        {
            slot = 0;
            if (TryGetSelectedSaveWindow(self, out var window) && window?.si != null)
            {
                if (!window.si.exists || !window.si.usable)
                    return false;

                slot = window.si.index;
                return slot >= 0;
            }

            if (!TryGetSelectedSaveIndex(self, out slot))
                return false;

            return dc.tool.File.Class.exists.Invoke(MakeHLString(GetOriginalSaveRelativeFilePath(slot)));
        }

        private static bool TryGetSelectedSaveIndex(SaveChoice self, out int slot)
        {
            slot = 0;
            if (self == null)
                return false;
            var saves = self.saves;
            if (saves == null)
                return false;

            var selectedIndex = self.curSaveId;
            if (selectedIndex < 0 || selectedIndex >= saves.length)
                return false;

            slot = selectedIndex;
            return true;
        }

        private static bool CopyOriginalSaveIntoMultiplayerSlot(int sourceSlot, int targetSlot)
        {
            try
            {
                var sourceRelativePath = GetOriginalSaveRelativeFilePath(sourceSlot);
                if (!dc.tool.File.Class.exists.Invoke(MakeHLString(sourceRelativePath)))
                {
                    _log?.Warning("[NetMod] No original save found for multiplayer import: {Path}", GetAbsoluteSavePath(sourceRelativePath));
                    return false;
                }

                var sourceBytes = dc.tool.File.Class.getBytes.Invoke(MakeHLString(sourceRelativePath));
                if (sourceBytes == null)
                {
                    _log?.Warning("[NetMod] Failed to read original save for multiplayer import: {Path}", GetAbsoluteSavePath(sourceRelativePath));
                    return false;
                }

                EnsureMultiplayerSaveFolderExists();
                var targetRelativePath = GetMultiplayerSaveRelativeFilePath(targetSlot);
                dc.tool.File.Class.saveBytes.Invoke(MakeHLString(targetRelativePath), sourceBytes);
                MUser.ClearCoopId(targetSlot);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to import original save into multiplayer slot: {Message}", ex.Message);
                return false;
            }
        }

        private static void SetCurrentSaveSlot(int slot)
        {
            try
            {
                var options = dc.Main.Class.ME?.options;
                if (options == null)
                    return;

                options.curSlot = slot;
                options.save();
            }
            catch (Exception ex)
            {
                _log?.Warning("[NetMod] Failed to set multiplayer save slot {Slot}: {Message}", slot + 1, ex.Message);
            }
        }
    }
}
