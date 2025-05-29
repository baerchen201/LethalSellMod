using System;
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
    private ConfigEntry<string> itemPriority = null!;
    internal Dictionary<string, int> ItemPriority = null!;

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
        itemPriority = Config.Bind(
            "Items",
            "ItemPriority",
            csv([]),
            "Items to sell before others (by internal name, comma-separated)"
        );
        ItemPriority = csv(itemPriority.Value)
            .Select((e, i) => new KeyValuePair<string, int>(e, i))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

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
                        break;
                    case "all":
                        value = null;
                        break;
                    default:
                        if (!int.TryParse(args[0], out var _value))
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
        var items = Object.FindObjectsOfType<GrabbableObject>();
        if (items == null || items.Length == 0)
            return null;

        items = FilterAndSortItems(items, desk);
        if (items.Length == 0)
            return null;
        if (items.Length == 1 || value == null)
            return items.ToList();
        value = (int)Math.Ceiling((double)(value / StartOfRound.Instance.companyBuyingRate));

        int n = items.Length;
        int bestDiff = int.MinValue;
        List<GrabbableObject> bestSubset = [];

        for (int i = 0; i < 1 << n; i++)
        {
            List<GrabbableObject> subset = [];
            int sum = 0;

            for (int j = 0; j < n; j++)
            {
                if ((i & (1 << j)) == 0)
                    continue;
                subset.Add(items[j]);
                sum += items[j].scrapValue;
            }

            int diff = value.Value - sum;
            if (diff <= bestDiff || diff > 0)
                continue;
            LethalSellMod.Logger.LogDebug(
                $"Found {(bestDiff == int.MinValue ? "" : diff == 0 ? "best " : "better ")}match: {subset.Count} items for {sum} value ({value.Value} requested, diff:{diff}, bestDiff:{bestDiff})"
            );
            if (diff == 0)
                return subset;
            bestDiff = diff;
            bestSubset = subset;
        }

        return bestSubset;
    }

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

    private static GrabbableObject[] FilterAndSortItems(
        GrabbableObject[] items,
        DepositItemsDesk desk
    )
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
            .OrderBy(i =>
                LethalSellMod.Instance.ItemPriority.GetValueOrDefault(
                    RemoveClone(i.name),
                    int.MaxValue
                )
            )
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
