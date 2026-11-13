using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools;
using OmenTools.Extensions;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TreeTools.Lifecycle;

namespace TreeTools;

public sealed unsafe class ItemDetailTooltipStateAdapter : IDisposable
{
    // 分类 1 至 4 是使用评级分组的陆行鸟登记与繁育记录。
    private static readonly HashSet<uint> ChocoboRegistrationItemIDs =
        LuminaGetter.Get<RacingChocoboItem>()
            .Where(row => row.Category is >= 1 and <= 4)
            .Select(row => row.Item.RowId)
            .Where(itemID => itemID != 0)
            .ToHashSet();

    private readonly AddonEventRegistry addonEvents;
    private bool disposed;

    public ItemDetailTooltipStateAdapter()
    {
        addonEvents = new(DService.Instance().AddonLifecycle);
        addonEvents.Register(AddonEvent.PreRequestedUpdate, ITEM_DETAIL_ADDON_NAME, OnItemDetailPreRequestedUpdate);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        addonEvents.Dispose();
    }

    private static void OnItemDetailPreRequestedUpdate(AddonEvent _, AddonArgs args)
    {
        var addon = (AddonItemDetail*)args.Addon.Address;
        if (addon == null)
            return;

        var itemID = ItemUtil.GetBaseId(AgentItemDetail.Instance()->ItemId).ItemId;
        if (itemID == 0 || ChocoboRegistrationItemIDs.Contains(itemID))
            return;

        var numberArrayData = AtkStage.Instance()->GetNumberArrayData(NumberArrayType.ItemDetail);
        var stringArrayData = AtkStage.Instance()->GetStringArrayData(StringArrayType.ItemDetail);
        if (numberArrayData == null || numberArrayData->IntArray == null ||
            stringArrayData == null || stringArrayData->StringArray == null)
            return;

        var flags = numberArrayData->IntArray[ITEM_DETAIL_FLAGS_INDEX];
        if ((flags & NATIVE_BONUSES_GROUP_FLAG) == 0)
            return;

        var isConsumable = LuminaGetter.TryGetRow<Item>(itemID, out var item) &&
                           item.ItemUICategory.RowId is 44 or 45 or 46;
        var flagsToSet = 0;
        if (isConsumable)
        {
            ref var effectText = ref stringArrayData->StringArray[(int)TooltipItemType.Effect];
            if (effectText.HasValue && !new ReadOnlySeString(new CStringPointer(effectText.Value).AsSpan()).IsEmpty)
                flagsToSet = NATIVE_EFFECTS_GROUP_FLAG;
        }
        else
        {
            var chocoboRatingTitle = LuminaWrapper.GetAddonText(CHOCOBO_RATING_TITLE_ROW_ID);
            if (chocoboRatingTitle.Length == 0)
                return;

            ref var titleText = ref stringArrayData->StringArray[(int)TooltipItemType.SpecialTitle];
            if (!titleText.HasValue)
                return;

            var title = new ReadOnlySeString(new CStringPointer(titleText.Value).AsSpan());
            if (!title.ExtractText().Equals(chocoboRatingTitle, StringComparison.Ordinal))
                return;
        }

        numberArrayData->IntArray[ITEM_DETAIL_FLAGS_INDEX] = (flags & ~NATIVE_BONUSES_GROUP_FLAG) | flagsToSet;
    }

    #region 常量

    private const string ITEM_DETAIL_ADDON_NAME = "ItemDetail";

    private const uint CHOCOBO_RATING_TITLE_ROW_ID = 8361;

    private const int ITEM_DETAIL_FLAGS_INDEX = 5;

    private const int NATIVE_EFFECTS_GROUP_FLAG = 1 << 8;

    private const int NATIVE_BONUSES_GROUP_FLAG = 1 << 4;

    #endregion
}
