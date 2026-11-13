using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace Godot
{
    public class PackedScene
    {
        public T Instantiate<T>() where T : new() => new();
    }
    public static class ResourceLoader
    {
        public static T Load<T>(string path) where T : new() => new();
    }
}
namespace MegaCrit.Sts2.Core.ControllerInput
{
    public static class MegaInput { public const string select = "select"; }
}
namespace MegaCrit.Sts2.Core.Combat
{
    public class CombatManager
    {
        public static readonly CombatManager Instance = new();
        public bool IsInProgress = true;
        public bool IsOverOrEnding;
        public bool PlayerActionsDisabled;
    }
}
namespace MegaCrit.Sts2.Core.Context
{
    public static class LocalContext { public static bool IsMe(Player player) => false; }
}
namespace MegaCrit.Sts2.Core.Entities.Cards
{
    public enum TargetType { None, AnyEnemy, AnyPlayer, AnyAlly, TargetedNoCreature }
    public class CardCreationResult { public CardModel Card; }
}
namespace MegaCrit.Sts2.Core.Entities.Creatures
{
    public class Player { public Creature Creature = new(); }
    public class Creature
    {
        public bool IsAlive = true;
        public bool IsPlayer;
        public Player Player;
        public int Side;
    }
}
namespace MegaCrit.Sts2.Core.Models
{
    public class CardModel
    {
        public string Id = "fixture-card";
        public TargetType TargetType;
        public Player Owner = new();
        public int ManualPlays;
        public bool ManualPlaySucceeds = true;
        public bool TryManualPlay(object target) { ManualPlays++; return ManualPlaySucceeds; }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.GodotExtensions
{
    public class NClickableControl : Control
    {
        public new static class SignalName
        {
            public const string MouseReleased = "mouse-released";
            public const string Released = "released";
        }
    }
    public class NButton : NClickableControl { }
}
namespace MegaCrit.Sts2.Core.Nodes.CommonUi
{
    public class NConfirmButton : NButton
    {
        public bool Enabled;
        public void Enable() { Enabled = true; Visible = true; }
        public void Disable() { Enabled = false; Visible = false; }
        // Emit even if disabled to exercise stale callbacks after a policy switch.
        public void Release() { Emit(SignalName.Released, this); }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Cards
{
    public class NCard : Control { public CardModel Model = new(); }
}
namespace MegaCrit.Sts2.Core.Nodes.Cards.Holders
{
    public class NCardHolder : Control
    {
        private bool _isHovered;
        private bool _isFocused;
        public NClickableControl Hitbox = new();
        public MegaCrit.Sts2.Core.Nodes.Cards.NCard CardNode = new();
        public CardModel CardModel => CardNode?.Model;
        public bool IsHovered => _isHovered;
        public bool IsFocused => _isFocused;
        public int HoverEffects;
        public void SetMouseHovered(bool hovered) { _isHovered = hovered; RefreshFocusState(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        protected void RefreshFocusState()
        {
            var focused = _isHovered || HasFocus;
            if (_isFocused != focused) { _isFocused = focused; DoCardHoverEffects(focused); }
        }
        protected void DoCardHoverEffects(bool hovered) { HoverEffects++; }
    }
    public class NHandCardHolder : NCardHolder
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Combat
{
    public class NPlayerHand : Control
    {
        public enum Mode { Play, Other }
        public static NPlayerHand Instance;
        public Mode CurrentMode = Mode.Play;
        public readonly PeekState PeekButton = new();
        public readonly List<NHandCardHolder> ActiveHolders = new();
        public bool InCardPlay;
        public bool AwaitingPlay;
        public int CardPlays;
        public int LayoutRefreshes;
        private int _lastFocusedHolderIdx;
        public NHandCardHolder FocusedHolder { get; private set; }
        public bool IsAwaitingPlay(NHandCardHolder holder) => AwaitingPlay;
        public void ForceRefreshCardIndices() { LayoutRefreshes++; }
        private bool AreCardActionsAllowed() => !MegaCrit.Sts2.Core.Combat.CombatManager.Instance.PlayerActionsDisabled;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnHolderPressed(NCardHolder holder) { StartCardPlay((NHandCardHolder)holder, false); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnHolderFocused(NHandCardHolder holder) { FocusedHolder = holder; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StartCardPlay(NHandCardHolder holder, bool startedViaShortcut) { CardPlays++; InCardPlay = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void CancelAllCardPlay() { InCardPlay = false; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnCombatEnded() { }
    }
    public class PeekState { public bool IsPeeking; }
    public class NCreature : Control
    {
        public NClickableControl Hitbox = new();
        public Creature Entity = new();
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Rooms
{
    public class NCombatRoom : Node { public static readonly NCombatRoom Instance = new(); }
}
namespace MegaCrit.Sts2.Core.Nodes.Screens.Overlays
{
    public class NOverlayStack { public static readonly NOverlayStack Instance = new(); public int ScreenCount; }
}
namespace MegaCrit.Sts2.Core.Runs
{
    public class RunManager
    {
        public static readonly RunManager Instance = new();
        public readonly HoveredModelTracker HoveredModelTracker = new();
#if STS2_TARGET_1130
        public readonly MegaCrit.Sts2.Core.Multiplayer.Game.MerchantSynchronizer MerchantSynchronizer = new();
#endif
    }
    public class HoveredModelTracker
    {
        public void OnLocalCardHovered(CardModel card) { }
        public void OnLocalCardUnhovered() { }
        public void OnLocalCardSelected(CardModel card) { }
        public void OnLocalCardDeselected() { }
    }
}
namespace MegaCrit.Sts2.Core.Nodes.Screens.CardSelection
{
    public class NCardRewardSelectionScreen : Control
    {
        private readonly TaskCompletionSource<int> _completionSource = new();
        private readonly List<CardCreationResult> _options = new();
        public Task<int> Completion => _completionSource.Task;
        public void AddOption(NCardHolder holder) { _options.Add(new CardCreationResult { Card = holder.CardModel }); AddChild(holder); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void RefreshOptions() { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SelectCard(NCardHolder cardHolder) { _completionSource.TrySetResult(_options.FindIndex(option => ReferenceEquals(option.Card, cardHolder.CardModel))); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnAlternateRewardSelected() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayClosed() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayHidden() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayShown() { }
    }
    public class NChooseACardSelectionScreen : Control
    {
        private readonly TaskCompletionSource<CardModel[]> _completionSource = new();
        private ulong _openedTicks;
        private bool _screenComplete;
        private bool _cardSelected;
        public Task<CardModel[]> Completion => _completionSource.Task;
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { _openedTicks = Time.GetTicksMsec(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SelectHolder(NCardHolder cardHolder) { _completionSource.TrySetResult(new[] { cardHolder.CardModel }); }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayClosed() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayHidden() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void AfterOverlayShown() { }
    }
}
namespace MegaCrit.Sts2.Core.Entities.Merchant
{
    public enum MerchantItem { Card, Relic, Potion, CardRemoval }

    public class MerchantInventory
    {
        public int Gold = 100;
        public bool PotionBeltFull;
        public bool PurchaseInProgress;
        public readonly List<MerchantItem> PurchasedItems = new();
        public event Action PurchaseCompleted;
        public void CompletePurchase() { PurchaseCompleted?.Invoke(); }
    }

    public class MerchantEntry
    {
        public readonly MerchantInventory Inventory;
        public readonly MerchantItem Item;
        public readonly int Cost = 10;
        public bool IsStocked = true;
        public bool RestockAfterPurchase;
        public int Purchases;
        public Exception PurchaseException;
        public TaskCompletionSource<bool> PurchaseDecision;

        public MerchantEntry(MerchantInventory inventory, MerchantItem item) { Inventory = inventory; Item = item; }

#if STS2_TARGET_1130
        public Task<bool> OnTryPurchaseUnsynchronized(MerchantInventory inventory, bool ignoreCost = false) => Purchase(inventory, ignoreCost);
#else
        public Task<bool> OnTryPurchaseWrapper(MerchantInventory inventory, bool ignoreCost = false) => Purchase(inventory, ignoreCost);
#endif

        private async Task<bool> Purchase(MerchantInventory inventory, bool ignoreCost)
        {
            if (!ReferenceEquals(inventory, Inventory)) throw new InvalidOperationException("Wrong merchant inventory.");
            if (PurchaseException != null) throw PurchaseException;
            if (!IsStocked || (!ignoreCost && inventory.Gold < Cost) || (Item == MerchantItem.Potion && inventory.PotionBeltFull))
                return false;
            if (PurchaseDecision != null && !await PurchaseDecision.Task)
                return false;
            if (!ignoreCost) inventory.Gold -= Cost;
            inventory.PurchasedItems.Add(Item);
            Purchases++;
            IsStocked = RestockAfterPurchase && Item != MerchantItem.CardRemoval;
            inventory.CompletePurchase();
            return true;
        }
    }
}
#if STS2_TARGET_1130
namespace MegaCrit.Sts2.Core.Multiplayer.Game
{
    public class MerchantSynchronizer
    {
        public async Task<bool> PurchaseLocalEntry(MerchantEntry entry)
        {
            var inventory = entry.Inventory;
            if (inventory.PurchaseInProgress) return false;
            inventory.PurchaseInProgress = true;
            try { return await entry.OnTryPurchaseUnsynchronized(inventory); }
            finally { inventory.PurchaseInProgress = false; }
        }
    }
}
#endif
namespace MegaCrit.Sts2.Core.Nodes.Screens.Shops
{
    public class NMerchantInventory : Control
    {
        private bool _isInputBlocked;
        public bool IsOpen = true;
        public readonly MerchantInventory Inventory = new();
        public NMerchantInventory() { Inventory.PurchaseCompleted += OnPurchaseCompleted; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void _Ready() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Open() { IsOpen = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void Close() { IsOpen = false; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnPurchaseCompleted() { }
        [MethodImpl(MethodImplOptions.NoInlining)] public void BlockInput() { _isInputBlocked = true; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void UnblockInput() { _isInputBlocked = false; }
        [MethodImpl(MethodImplOptions.NoInlining)] public void OnActiveScreenUpdated() { }
    }
    public abstract class NMerchantSlot : Control
    {
        private readonly NMerchantInventory _merchantRug;
        private bool _isHovered = true;
        private bool _ignoreMouseRelease;
        public MerchantEntry Entry { get; }
        public int Purchases => Entry.Purchases;
        protected NMerchantSlot(NMerchantInventory inventory, MerchantItem item)
        {
            _merchantRug = inventory;
            Entry = new MerchantEntry(inventory.Inventory, item);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void _GuiInput(InputEvent inputEvent) { if (inputEvent.IsActionPressed("select")) OnSelected().GetAwaiter().GetResult(); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnMouseReleased(InputEvent inputEvent)
        {
            if (_isHovered && !_ignoreMouseRelease && inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left })
                OnSelected().GetAwaiter().GetResult();
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task OnSelected()
        {
            ClearHoverTip();
#if STS2_TARGET_1130
            await RunManager.Instance.MerchantSynchronizer.PurchaseLocalEntry(Entry);
#else
            await OnTryPurchase(_merchantRug.Inventory);
#endif
        }
#if !STS2_TARGET_1130
        protected abstract Task OnTryPurchase(MerchantInventory inventory);
#endif
        private void ClearHoverTip() { }
    }
    public class NMerchantCard : NMerchantSlot
    {
        public NMerchantCard(NMerchantInventory inventory) : base(inventory, MerchantItem.Card) { }
#if !STS2_TARGET_1130
        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override async Task OnTryPurchase(MerchantInventory inventory) { await Entry.OnTryPurchaseWrapper(inventory); }
#endif
    }
    public class NMerchantRelic : NMerchantSlot
    {
        public NMerchantRelic(NMerchantInventory inventory) : base(inventory, MerchantItem.Relic) { }
#if !STS2_TARGET_1130
        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override async Task OnTryPurchase(MerchantInventory inventory) { await Entry.OnTryPurchaseWrapper(inventory); }
#endif
    }
    public class NMerchantPotion : NMerchantSlot
    {
        public NMerchantPotion(NMerchantInventory inventory) : base(inventory, MerchantItem.Potion) { }
#if !STS2_TARGET_1130
        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override async Task OnTryPurchase(MerchantInventory inventory) { await Entry.OnTryPurchaseWrapper(inventory); }
#endif
    }
    public class NMerchantCardRemoval : NMerchantSlot
    {
        public NMerchantCardRemoval(NMerchantInventory inventory) : base(inventory, MerchantItem.CardRemoval) { }
#if !STS2_TARGET_1130
        [MethodImpl(MethodImplOptions.NoInlining)]
        protected override async Task OnTryPurchase(MerchantInventory inventory) { await Entry.OnTryPurchaseWrapper(inventory); }
#endif
    }
}
