using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ChatCommandAPI;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LethalSellMod;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("baer1.ChatCommandAPI")]
public class LethalSellMod : BaseUnityPlugin
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public static LethalSellMod Instance { get; private set; } = null!;
    internal static new ManualLogSource Logger { get; private set; } = null!;

    private ConfigEntry<string> itemBlacklist = null!;
    internal string[] ItemBlacklist => csv(itemBlacklist.Value);

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        itemBlacklist = Config.Bind(
            "Items",
            "ItemBlacklist",
            csv(["ShotgunItem"]),
            "Items to never sell by internal name (comma-separated)"
        );
        _ = new SellCommand();

        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} has loaded!");
    }

    private static string csv(string[] e) => e.Join(null, ",");

    private static string[] csv(string e) => e.Split(',');
}

public class SellCommand : Command
{
    public override string Name => "Sell";
    public override string Description => "Sells items on 71-Gordion";
    public override string[] Syntax => ["[value]", "{ quota | all }"];

    public override bool Invoke(string[] args, Dictionary<string, string> kwargs, out string error)
    {
        error = "Invalid arguments";
        int itemCount,
            totalValue;
        switch (args.Length)
        {
            case 0:
                if (!OpenDoor(out error, out itemCount, out totalValue))
                    return false;
                ChatCommandAPI.ChatCommandAPI.Print(
                    $"Selling {a(itemCount)} with a total value of {b(totalValue)}"
                );
                return true;
            case 1:
                int? value;
                switch (args[0].ToLower())
                {
                    case "quota":
                        value = TimeOfDay.Instance.profitQuota - TimeOfDay.Instance.quotaFulfilled;
                        error = "Quota is already fulfilled";
                        if (value < 1)
                            return false;
                        break;
                    case "all":
                        value = null;
                        break;
                    default:
                        if (!int.TryParse(args[0], out var _value))
                            return false;
                        error = "Value must be a positive integer";
                        if (_value < 1)
                            return false;
                        value = _value;
                        break;
                }

                error = "Selling desk not found";
                DepositItemsDesk desk = Object.FindObjectOfType<DepositItemsDesk>();
                if (GameNetworkManager.Instance == null || desk == null)
                    return false;
                error = "No items found";
                var items = ItemsForValue(value, desk);
                if (items == null)
                    return false;
                error = "You can't afford to sell that amount";
                if (items.Count == 0)
                    return false;

                error = "Error selling items";
                if (!SellItems(items, desk, out itemCount))
                    return false;
                if (!desk.doorOpen && value == null)
                {
                    if (!OpenDoor(out _, out itemCount, out totalValue))
                        return false;
                    ChatCommandAPI.ChatCommandAPI.Print(
                        $"Selling {a(itemCount)} with a total value of {b(totalValue)}"
                    );
                }
                else
                {
                    ChatCommandAPI.ChatCommandAPI.Print(
                        $"Put {a(itemCount)} worth {b(items)} on the desk"
                    );
                }
                return true;
            default:
                return false;
        }
    }

    private static string a(int itemCount) => itemCount + (itemCount == 1 ? " item" : " items");

    private static string b(List<GrabbableObject> items) =>
        items.Sum(i => i.scrapValue)
        + (
            Mathf.Approximately(StartOfRound.Instance.companyBuyingRate, 1f)
                ? ""
                : $" ({(int)(items.Sum(i => i.scrapValue) * StartOfRound.Instance.companyBuyingRate)})"
        );

    private static string b(int totalValue) =>
        totalValue
        + (
            Mathf.Approximately(StartOfRound.Instance.companyBuyingRate, 1f)
                ? ""
                : $" ({(int)(totalValue * StartOfRound.Instance.companyBuyingRate)})"
        );

    private static List<GrabbableObject>? ItemsForValue(int? value, DepositItemsDesk desk)
    {
        LethalSellMod.Logger.LogDebug(
            $">> ItemsForValue({(value == null ? "null" : value)}, {desk})"
        );

        if (value is 0)
        {
            LethalSellMod.Logger.LogDebug("<< ItemsForValue -> null (no value)");
            return null;
        }

        var items = Object.FindObjectsOfType<GrabbableObject>();
        LethalSellMod.Logger.LogDebug(
            $"   max value: {items.Sum(i => i.scrapValue)} items:{c(items)}"
        );
        if (items == null || items.Length == 0)
        {
            LethalSellMod.Logger.LogDebug(
                $"<< ItemsForValue -> null (items is empty) items:{c(items)}"
            );
            return null;
        }

        items = FilterItems(items, desk);
        LethalSellMod.Logger.LogDebug(
            $"   post-filter max value: {items.Sum(i => i.scrapValue)} items:{c(items)}"
        );
        if (items.Length == 0)
        {
            LethalSellMod.Logger.LogDebug(
                $"<< ItemsForValue -> null (filtered items is empty) items:{c(items)}"
            );
            return null;
        }
        if (value == null)
        {
            LethalSellMod.Logger.LogDebug(
                $"<< ItemsForValue -> {c(items)} (no calculations required > all)"
            );
            return items.ToList();
        }
        if (items.Length == 1 && items[0].scrapValue >= value)
        {
            LethalSellMod.Logger.LogDebug(
                $"<< ItemsForValue -> {c(items)} (no calculations required > 1 item)"
            );
            return items.ToList();
        }
        value = (int)Math.Ceiling((double)(value / StartOfRound.Instance.companyBuyingRate));

        List<GrabbableObject> bestSubset = [];

        LethalSellMod.Logger.LogDebug(
            SmartCalculation(items, value.Value, ref bestSubset)
                ? $"<< ItemsForValue -> {c(bestSubset)} (found best match)"
                : $"<< ItemsForValue -> {c(bestSubset)}"
        );
        return bestSubset;
    }

    private static bool SmartCalculation(
        GrabbableObject[] items,
        int value,
        // ReSharper disable once RedundantAssignment
        ref List<GrabbableObject> bestSubset
    )
    {
        items = items.OrderByDescending(i => i.scrapValue).ToArray();

        int bestDiff = int.MinValue;
        int baseSum = 0;
        List<int> baseItems = [];

        while (baseSum <= value - 50)
        {
            bool _continue = false;
            for (int i = 0; i < items.Length; i++)
                if (
                    !baseItems.Contains(i)
                    && (
                        baseSum + items[i].scrapValue <= Math.Max(value - 50, 0)
                        || baseSum + items[i].scrapValue == value
                    )
                )
                {
                    baseSum += items[i].scrapValue;
                    int diff = value - 50 - baseSum;
                    baseItems.Add(i);
                    LethalSellMod.Logger.LogDebug(
                        $"   Found {(bestDiff == int.MinValue ? "" : diff == 0 ? "best " : "better ")}base match: {baseItems.Count} items for {baseSum} value ({value} requested, diff:{diff}, bestDiff:{bestDiff})"
                    );
                    bestDiff = diff;
                    _continue = true;
                    break;
                }

            if (_continue)
                continue;
            break;
        }

        int missingValue = value - baseSum;
        bestDiff = -missingValue;
        var baseSubset = baseItems.Select(i => items[i]).ToList();
        bestSubset = baseSubset;
        items = items.Where((_, i) => !baseItems.Contains(i)).ToArray();
        LethalSellMod.Logger.LogDebug(
            $"   Finished base calculation ({baseItems.Count} items for {baseSum} value, missing {missingValue} value, got {items.Length} items to work with)"
        );

        if (missingValue == 0)
            return true;

        if (items.Length == 0)
        {
            if (missingValue > 0)
                bestSubset = [];
            return false;
        }
        for (int i = 0; i < (int)Math.Pow(2, items.Length); i++)
        {
            int sum = items.Where((_, j) => (i & (int)Math.Pow(2, j)) != 0).Sum(t => t.scrapValue);

            int diff = missingValue - sum;
            if (diff > 0 || diff <= bestDiff)
                continue;
            bestSubset = [];
            bestSubset.AddRange(items.Where((_, j) => (i & (int)Math.Pow(2, j)) != 0));
            LethalSellMod.Logger.LogDebug(
                $"   Found {(bestDiff == int.MinValue ? "" : diff == 0 ? "best " : "better ")}match: {bestSubset.Count + baseItems.Count} items for {sum + baseSum} value ({value} requested, diff:{diff}, bestDiff:{bestDiff})"
            );
            if (diff == 0)
            {
                bestSubset.AddRange(baseSubset);
                return true;
            }
            bestDiff = diff;
        }

        if (bestDiff > 0)
        {
            bestSubset = [];
            return false;
        }
        bestSubset.AddRange(baseSubset);
        return false;
    }

    private static string c(IList? list) =>
        list == null
            ? "null"
            : $"{list.GetType().Name}{(list.GetType().GetGenericArguments().Length == 0 ? "" : $"<{list.GetType().GetGenericArguments().Join()}>")}[{list.Count}]";

    private static bool SellItems(
        List<GrabbableObject> items,
        DepositItemsDesk desk,
        out int itemCount
    )
    {
        GameNetworkManager.Instance.localPlayerController.DropAllHeldItemsAndSync();
        itemCount = 0;
        foreach (var i in items)
        {
            if (i == null)
                continue;
            itemCount++;

            Vector3 vector = RoundManager.RandomPointInBounds(desk.triggerCollider.bounds);
            vector.y = desk.triggerCollider.bounds.min.y;
            if (
                Physics.Raycast(
                    new Ray(vector + Vector3.up * 3f, Vector3.down),
                    out var hitInfo,
                    8f,
                    1048640,
                    QueryTriggerInteraction.Collide
                )
            )
            {
                vector = hitInfo.point;
            }
            vector.y += i.itemProperties.verticalOffset;
            vector = desk.deskObjectsContainer.transform.InverseTransformPoint(vector);

            desk.AddObjectToDeskServerRpc(i.NetworkObject);
            GameNetworkManager.Instance.localPlayerController.PlaceGrabbableObject(
                desk.deskObjectsContainer.transform,
                vector,
                false,
                i
            );
            GameNetworkManager.Instance.localPlayerController.PlaceObjectServerRpc(
                i.NetworkObject,
                desk.deskObjectsContainer,
                vector,
                false
            );
        }

        return true;
    }

    private const string CLONE = "(Clone)";

    public static string RemoveClone(string name) =>
        name.EndsWith(CLONE) ? name[..^CLONE.Length] : name;

    private static GrabbableObject[] FilterItems(GrabbableObject[] items, DepositItemsDesk desk)
    {
        return items
            .Where(i =>
                i
                    is {
                        scrapValue: > 0,
                        isHeld: false,
                        isPocketed: false,
                        itemProperties.isScrap: true
                    }
                && !desk.itemsOnCounter.Contains(i)
            )
            .Where(i => !LethalSellMod.Instance.ItemBlacklist.Contains(RemoveClone(i.name)))
            .ToArray();
    }

    private static bool OpenDoor(out string error, out int itemCount, out int totalValue)
    {
        itemCount = 0;
        totalValue = 0;
        error = "Selling desk not found";
        DepositItemsDesk desk = Object.FindObjectOfType<DepositItemsDesk>();
        if (GameNetworkManager.Instance == null || desk == null)
            return false;
        itemCount = desk.itemsOnCounter.Count;
        totalValue = desk.itemsOnCounter.Sum(i => i.scrapValue);
        error = "No items on desk";
        if (itemCount == 0)
            return false;
        error = "Door already open";
        if (desk.doorOpen)
            return false;

        desk.SetTimesHeardNoiseServerRpc(5f);
        return true;
    }
}
