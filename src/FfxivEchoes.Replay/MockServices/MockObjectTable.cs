using System.Collections.Generic;
using System.Numerics;
using FfxivEchoes.Events;

namespace FfxivEchoes.Replay.MockServices;

/// <summary>
/// jsonl の <c>object_appear</c> / <c>object_disappear</c> を内部マップに反映する軽量実装。
/// </summary>
/// <remarks>
/// <para>
/// 本来は Dalamud の <c>IObjectTable</c> を実装したいところだが、IObjectTable は
/// <c>IGameObject</c> インスタンスを返すため、Dalamud 内部の <c>GameObject</c>
/// (ネイティブ ptr ベース) を mock するのは現実的でない。
/// Phase 1 では「どの actor が active か」を保持するだけの簡易ストレージに留め、
/// AoE サービスが actor 位置を必要としたら future agent が拡張する。
/// </para>
/// <para>
/// 内部 API は <see cref="TryGet"/> / <see cref="Active"/> のみ。
/// JsonlReplayer から <see cref="Register"/> / <see cref="Unregister"/> で更新される。
/// </para>
/// </remarks>
public sealed class MockObjectTable
{
    private readonly Dictionary<uint, ActorState> _byObjectId = new();
    private readonly object _gate = new();

    public sealed record ActorState(uint ObjectId, string Name, uint DataId, Vector3 Position);

    public void Register(ObjectAppearedEvent ev)
    {
        lock (_gate)
        {
            _byObjectId[ev.ObjectId] = new ActorState(ev.ObjectId, ev.ObjectName, ev.DataId, ev.Position);
        }
    }

    public void Unregister(uint objectId)
    {
        lock (_gate)
        {
            _byObjectId.Remove(objectId);
        }
    }

    public ActorState? TryGet(uint objectId)
    {
        lock (_gate)
        {
            return _byObjectId.TryGetValue(objectId, out var v) ? v : null;
        }
    }

    public IReadOnlyCollection<ActorState> Active
    {
        get
        {
            lock (_gate)
            {
                return _byObjectId.Values.ToArray();
            }
        }
    }
}

internal static class CollectionExtensions
{
    public static T[] ToArray<T>(this Dictionary<uint, T>.ValueCollection values)
    {
        var arr = new T[values.Count];
        var i = 0;
        foreach (var v in values) arr[i++] = v;
        return arr;
    }
}
