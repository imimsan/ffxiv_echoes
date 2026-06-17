using System;

namespace FfxivEchoes.Recording;

/// <summary>
/// PC ジョブ専用スキル / 召喚物の名前パターン filter。
/// 録画ファイルに source 情報が無い / object_id 不一致で party filter が漏らした場合の
/// 最終防衛線。タイムライン (RecordingPredictionPlanner) と
/// mechanic 下書き (StrategyDraftGenerator) の両方から共用される。
/// </summary>
/// <remarks>
/// 「ボスがこの名前のキャストをするケース」がほぼ無い前提のキーワードのみを採用。
/// 増やしすぎるとボスギミックを誤フィルタするので、報告に基づいて慎重に追加。
/// </remarks>
public static class PcSkillNameFilter
{
    /// <summary>
    /// status_gain / status_update / status_lose に出る PC ジョブステータス語彙。
    /// </summary>
    private static readonly string[] StatusKeywords =
    {
        // 共通 / 全ジョブ
        "実行可", "コンボ", "強化薬",

        // タンク共通
        "スタンス",  // グリットスタンス (GNB) / アイアンウィル (PLD) / ディフェンダー (WAR) / ダークサイド (DRK) - "スタンス" 含む語句

        // SAM
        "の型", "陣ノ三", "陣ノ二", "天輪", "明鏡止水", "三色",

        // NIN
        "忍術", "忍隠", "風遁", "土遁", "水遁", "天地人", "夢幻三段", "影身",

        // MNK
        "踏鳴", "桃園", "金剛", "陰陽闘気", "サベッジクロウ",

        // BRD
        "メヌエット", "バラード", "パイオン",
        "ストームバイト", "ヴェノムバイト", "ウィンドバイト", "シャドウバイト",
        "レゾナント・アロー",

        // DRK
        "トアクリーバー", "ブラッドウェポン", "ダークマインド",

        // GNB
        "グリット", "ロイヤルガード", "ガンマスター", "ガンナー", "踊り狂い",
        "ノーマーシー", "ジャストモード",

        // RDM
        "デュエリスト", "マナフィケーション",

        // RDM / BRD / MCH 共通
        "アクセラレーション",

        // SCH
        "エーテルフロー", "光輝", "ジ・アート・オブ",

        // WHM
        // 「ディア」単体は "ディアI/II/III" 専用ステータス名なので具体的に列挙
        // （「ディアボロス」等のボス技に "ディア" を含む可能性を排除）
        "ディアI", "ディアII", "ディアIII",

        // RPR
        "ブラッドソイル", "ソウルソル", "デスシュラウド", "シャドウオブデス",

        // SMN
        // 「コンバ」「搭乗」単体だと "コンバット" "搭乗準備" 以外のボス技を巻き込む恐れがあるため
        // 具体的な PC ステータス名で列挙
        "コンバスト", "コンバラント", "コンバージェンス",
        "搭乗準備", "搭乗中", "搭乗解除",

        // VPR
        "蛇鋭牙", "蠱毒法",

        // PCT
        "祖霊", "ライブペイント", "ブリフルジェンス",
        "イマジンスカイ", "イマジンウォーター", "イマジンファイア",
        "クリーチャーモチーフ", "ハンマーモチーフ",

        // AST
        "ディヴィネーション", "アーサリースター", "ヘリオス", "アーセイクル",
        "アスペクトベネフィク", "アーチャー", "ボーラー", "ジ・エンペラー",

        // DNC
        "シルバー", "ゴールド", "サンバ",
    };

    /// <summary>
    /// cast_start / action_used に出る PC ジョブアクション / PC ペットの名前語彙。
    /// 「クイーン・ローラーダッシュ」のような pet スキルもここに入れる。
    /// </summary>
    private static readonly string[] CastActionKeywords =
    {
        // ペットスキル（owner check が漏れた場合のフォールバック）
        "クイーン・", "クイーン", "ロボット", "オートマトン",
        "オートタレット", "ルークタレット", "ビショップタレット",
        "カーバンクル", "バハムート", "フェニックス", "ガルーダ・エギ",
        "イフリート・エギ", "タイタン・エギ",
        "ガルーダエギ", "イフリートエギ", "タイタンエギ",
        "フェアリー", "妖精",
        "妖精ノクターナ", "妖精セレネ", "妖精エオス",
        "フェアリー・エオス", "フェアリー・セレネ",
        "アサイラム", "テトラグラマトン",
        "影身", "英雄の影身",  // NIN / RPR

        // PC アクション（status と一部重複するが cast_start 経路用に残す）
        "夢幻三段", "サベッジクロウ", "トアクリーバー",
        "ブリフルジェンス", "祖霊の蛇",
        "メヌエット", "バラード", "パイオン",
        "ハンマーコンボ", "ロイエ", "ルクス・ソラリス",
        "祖霊降ろし", "イマジンスカイ", "ディヴィネーション",
        "蛇鋭牙", "蠱毒法",
        // タンクスタンス（cast の場合もある）
        "グリット", "アイアンウィル", "ディフェンダー", "ダークサイド",
    };

    /// <summary>
    /// PC ペット名パターン（hp_change / object_appear）。
    /// </summary>
    private static readonly string[] PetNamePatterns =
    {
        // SMN
        "カーバンクル", "バハムート", "フェニックス",
        "ガルーダ・エギ", "イフリート・エギ", "タイタン・エギ",
        "ガルーダエギ", "イフリートエギ", "タイタンエギ",
        // SCH 妖精：日本語クライアントは「フェアリー」表記のことが多い
        "フェアリー", "妖精",
        "妖精ノクターナ", "妖精セレネ", "妖精エオス",
        "フェアリー・エオス", "フェアリー・セレネ",
        // MCH
        "オートタレット", "ルークタレット", "ビショップタレット", "クイーン",
        "クイーン・タレット", "オートマトン",
        // SGE / WHM フィールド
        "アサイラム", "テトラグラマトン", "リリーベル",
        // NIN / RPR の影身系
        "影身", "英雄の影身",
    };

    /// <summary>
    /// 候補ラベルが PC ジョブ由来かどうかを判定。マッチすれば mechanic / timeline 候補から除外。
    /// </summary>
    public static bool LooksLikePcSkillOrPet(string? label, string eventType)
    {
        if (string.IsNullOrEmpty(label)) return false;

        if (eventType is "status_gain" or "status_update" or "status_lose")
        {
            foreach (var kw in StatusKeywords)
            {
                if (label.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (eventType is "cast_start" or "action_used" or "auto_attack")
        {
            foreach (var kw in CastActionKeywords)
            {
                if (label.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (eventType is "hp_change" or "object_appear")
        {
            foreach (var pet in PetNamePatterns)
            {
                if (label.Contains(pet, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
