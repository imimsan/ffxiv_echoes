namespace FfxivEchoes.Recording;

/// <summary>
/// 録画 meta に含めるパーティメンバー情報。
/// </summary>
/// <param name="Name">プレイヤー名（自分は「自分」）</param>
/// <param name="Job">ジョブ略称（BLM / PLD / SAM など）</param>
/// <param name="Role">ロール大区分（Tank / Healer / DPS）</param>
/// <param name="SubRole">サブロール（mt / st / h1 / h2 / melee / ranged / caster）。決定不能なら null。</param>
public sealed record PartyMemberInfo(
    string Name,
    string Job,
    string Role,
    string? SubRole,
    uint? ObjectId = null,
    bool IsSelf = false);
