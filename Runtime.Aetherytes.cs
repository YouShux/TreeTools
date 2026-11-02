using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using OmenTools.Extensions;
using OmenTools.Info.Game.AetheryteRecord;
using OmenTools.Info.Game.AetheryteRecord.Data;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TreeTools.Threading;
using Action = System.Action;
using Map = Lumina.Excel.Sheets.Map;

namespace TreeTools;

public sealed partial class Runtime
{
    private static readonly Action<AetheryteRecordManager, Dictionary<string, List<AetheryteRecord>>> SetAetheryteRecords =
        typeof(AetheryteRecordManager).GetProperty(nameof(AetheryteRecordManager.Records), MEMBER_FLAGS)!.SetMethod!
            .CreateDelegate<Action<AetheryteRecordManager, Dictionary<string, List<AetheryteRecord>>>>();
    private static readonly Action<AetheryteRecordManager> BuildFirmamentRecords = AetheryteMethod("BuildFirmamentRecords");
    private static readonly Action<AetheryteRecordManager> BuildCosmicRecords = AetheryteMethod("BuildCosmicExplorationRecords");
    private static readonly Action<AetheryteRecordManager> BuildHouseRecords = AetheryteMethod("BuildHouseRecords");

    private static Action<AetheryteRecordManager> AetheryteMethod(string name) =>
        typeof(AetheryteRecordManager).GetMethod(name, MEMBER_FLAGS)?.CreateDelegate<Action<AetheryteRecordManager>>()
        ?? throw new MissingMethodException(typeof(AetheryteRecordManager).FullName, name);

    /// <summary>准备冷路径的方法代码与住宅文本格式，不读取游戏状态或游戏数据。</summary>
    private static void PrepareAetheryteCode(CancellationToken cancellationToken)
    {
        var buildType = typeof(AetheryteBuild);
        var recordType = typeof(AetheryteRecord);
        MethodBase?[] methods =
        [
            buildType.GetConstructor(MEMBER_FLAGS, [typeof(AetheryteRecordManager)]),
            buildType.GetMethod(nameof(AetheryteBuild.Add), MEMBER_FLAGS),
            buildType.GetMethod("BuildSubIndices", MEMBER_FLAGS),
            typeof(AetheryteRecords).TypeInitializer,
            BuildFirmamentRecords.Method,
            BuildCosmicRecords.Method,
            BuildHouseRecords.Method,
            typeof(AetheryteRecordManager).GetMethod("TryAddHouseRecord", MEMBER_FLAGS),
            recordType.GetMethod(nameof(AetheryteRecord.Update), MEMBER_FLAGS),
            recordType.GetMethod(nameof(AetheryteRecord.GetAetheryteState), MEMBER_FLAGS)
        ];
        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeHelpers.PrepareMethod((method ?? throw new MissingMethodException("Aetheryte startup method is unavailable.")).MethodHandle);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, object> arguments = new()
        {
            ["zoneName"] = string.Empty,
            ["houseType"] = string.Empty,
            ["ward"] = 1,
            ["plot"] = 1
        };
        string.FormatICUText("{zoneName}{houseType}{ward}{plot}", arguments);
    }

    private Task BuildAetheryteRecordsAsync
    (
        AetheryteRecordManager service,
        CancellationToken cancellationToken
    ) => FrameworkWorkScheduler.RunAsync(async () =>
    {
        while (true)
        {
            try
            {
                AetheryteBuild? pending = null;
                await FrameworkWorkScheduler.RunAsync(BuildSteps(), () => pending?.CheckState(), cancellationToken);

                IEnumerable<Action> BuildSteps()
                {
                    yield return () => pending = new(service);
                    var build = pending ?? throw new InvalidOperationException("Aetheryte preparation did not complete.");
                    foreach (var data in build.Rows)
                        yield return () => build.Add(data);
                    if (build.LoggedIn)
                    {
                        yield return () => BuildFirmamentRecords(build.Staging);
                        yield return () => BuildCosmicRecords(build.Staging);
                        yield return () => BuildHouseRecords(build.Staging);
                    }
                    foreach (var record in build.Staging.AllRecords)
                        yield return record.Update;
                    yield return () =>
                    {
                        build.CheckState();
                        SetAetheryteRecords(service, build.Staging.Records);
                        var gameState = GameState.Instance();
                        var type = typeof(AetheryteRecordManager);
                        IClientState.Instance().TerritoryChanged += type.GetMethod("OnZoneChanged", MEMBER_FLAGS)!
                            .CreateDelegate<Action<uint>>(service);
                        gameState.Login += type.GetMethod("OnLogin", MEMBER_FLAGS)!.CreateDelegate<Action>(service);
                        gameState.WorldChanged += type.GetMethod("OnWorldChanged", MEMBER_FLAGS)!.CreateDelegate<Action<uint>>(service);
                    };
                }
                return;
            }
            catch (AetheryteStateChangedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }, cancellationToken);

    private sealed class AetheryteStateChangedException : Exception;

    private sealed class AetheryteBuild
    {
        private readonly (bool LoggedIn, ulong Character, uint World, uint HomeWorld, uint Territory) identity;
        private readonly Dictionary<byte, Aetheryte[]> groups;
        private readonly Dictionary<byte, Dictionary<uint, byte>> subIndices = [];
        private readonly Dictionary<uint, Dictionary<string, Vector2>> markerPositions = [];
        private readonly IReadOnlyDictionary<uint, Vector3> redirectedPositions;
        private readonly IReadOnlyDictionary<uint, byte> redirectedVersions;
        private readonly string other;
        private ILookup<uint, MapMarker>? markersByRange;
        private ILookup<uint, Map>? mapsByTerritory;

        internal AetheryteBuild(AetheryteRecordManager service)
        {
            identity = CaptureIdentity();
            Staging = new(GameState.Instance())
            {
                HouseApartmentTemplate = service.HouseApartmentTemplate,
                HouseEstateTemplate = service.HouseEstateTemplate,
                HouseSharedName = service.HouseSharedName,
                HouseFreeCompanyName = service.HouseFreeCompanyName,
                HousePersonalName = service.HousePersonalName
            };
            var rows = LoggedIn ? LuminaGetter.Get<Aetheryte>().ToArray() : [];
            groups = rows.GroupBy(data => data.AethernetGroup).ToDictionary(group => group.Key, group => group.ToArray());
            Rows = rows.OrderBy(data => data.AethernetGroup).ThenBy(data => data.Territory.RowId).ToArray();
            other = (string)typeof(AetheryteRecordManager).GetField("VERSION_OTHER", MEMBER_FLAGS)!.GetValue(null)!;
            redirectedPositions = (IReadOnlyDictionary<uint, Vector3>)typeof(AetheryteRecords)
                .GetField("NormalPositions", MEMBER_FLAGS)!.GetValue(null)!;
            redirectedVersions = (IReadOnlyDictionary<uint, byte>)typeof(AetheryteRecords)
                .GetField("NormalVersions", MEMBER_FLAGS)!.GetValue(null)!;
        }

        internal AetheryteRecordManager Staging { get; }
        internal Aetheryte[] Rows { get; }
        internal bool LoggedIn => identity.LoggedIn;

        internal void CheckState()
        {
            if (CaptureIdentity() != identity)
                throw new AetheryteStateChangedException();
        }

        private static (bool, ulong, uint, uint, uint) CaptureIdentity()
        {
            if (!Current.Framework.IsInFrameworkUpdateThread)
                throw new InvalidOperationException("Aetheryte initialization requires the framework thread.");
            return GameState.IsLoggedIn
                ? (true, LocalPlayerState.ContentID, GameState.CurrentWorld, GameState.HomeWorld, GameState.TerritoryType)
                : (false, 0, 0, 0, 0);
        }

        internal void Add(Aetheryte data)
        {
            if (data.RowId <= 1 || data.PlaceName.RowId is 1145 or 1160)
                return;
            var map = data.Map.RowId != 0 ? data.Map.Value : data.Territory.Value.Map.Value;
            var name = data.IsAetheryte ? data.PlaceName.Value.Name.ToString() : data.AethernetName.Value.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                return;
            byte subIndex = 0;
            if (!data.IsAetheryte && data.AethernetGroup > 0)
            {
                if (!subIndices.TryGetValue(data.AethernetGroup, out var indices))
                    subIndices.Add(data.AethernetGroup, indices = BuildSubIndices(groups[data.AethernetGroup]));
                subIndex = indices.GetValueOrDefault(data.RowId, byte.MaxValue);
            }
            Vector3 position = default;
            if (redirectedPositions.TryGetValue(data.RowId, out var redirected))
                position = redirected;
            else if (LuminaGetter.TryGetRow<Level>(data.Level[0].RowId, out var level))
                position = level.GetPosition();
            else
            {
                if (!markerPositions.TryGetValue(data.Territory.RowId, out var positions))
                {
                    positions = new(StringComparer.Ordinal);
                    markersByRange ??= LuminaGetter.GetSub<MapMarker>().SelectMany(group => group).ToLookup(marker => marker.RowId);
                    mapsByTerritory ??= LuminaGetter.Get<Map>().ToLookup(entry => entry.TerritoryType.RowId);
                    foreach (var entry in mapsByTerritory[data.Territory.RowId])
                    foreach (var marker in markersByRange[entry.MapMarkerRange])
                        if (AetheryteRecord.TryParseName(marker, out var markerName))
                            positions.TryAdd(markerName, new(marker.X, marker.Y));
                    markerPositions.Add(data.Territory.RowId, positions);
                }
                if (positions.TryGetValue(name, out var coordinates))
                    position = PositionHelper.TextureToWorld(coordinates, map).ToVector3(0);
            }
            uint version = redirectedVersions.TryGetValue(data.RowId, out var redirectedVersion)
                ? redirectedVersion : data.Territory.Value.ExVersion.RowId;
            var record = new AetheryteRecord(data.RowId, subIndex, data.AethernetGroup, version, data.Territory.RowId,
                map.RowId, data.IsAetheryte, position, name);
            if (!record.IsUnlocked())
                return;
            var group = other;
            if (record.Group != 5)
            {
                if (record.Version == 0)
                {
                    var region = record.GetZone().PlaceNameRegion.Value;
                    if (region.RowId is 22 or 23 or 24)
                        group = region.Name.ToString();
                }
                else
                    group = $"{record.Version + 2}.0";
            }
            if (!Staging.Records.TryGetValue(group, out var list))
                Staging.Records.Add(group, list = []);
            list.Add(record);
        }

        private static Dictionary<uint, byte> BuildSubIndices(Aetheryte[] rows)
        {
            var special = rows.Where(data => !data.IsAetheryte && data.Map.RowId > 0)
                .OrderBy(data => data.Territory.RowId).ThenBy(data => data.RowId).ToArray();
            var remaining = rows.ExceptBy(special.Select(data => data.RowId), data => data.RowId).ToArray();
            var main = remaining.FirstOrDefault(data => data.IsAetheryte);
            var outdoors = remaining.Where(data => LuminaGetter.TryGetRow<Level>(data.Level[0].RowId, out _))
                .OrderBy(data => data.Territory.RowId).ThenBy(data => data.RowId).ToArray();
            var left = remaining.ExceptBy(outdoors.Select(data => data.RowId), data => data.RowId)
                .Where(data => !data.IsAetheryte).OrderBy(data => data.RowId);
            Dictionary<uint, byte> result = [];
            var index = 0;
            // 上游始终保留主水晶槽位；重复行取首次位置，保持原生以太网索引。
            foreach (var data in new[] { main }.Concat(left).Concat(outdoors).Concat(special))
                result.TryAdd(data.RowId, (byte)index++);
            return result;
        }
    }
}
