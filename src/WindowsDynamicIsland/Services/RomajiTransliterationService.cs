using System.Text;
using Windows.Globalization;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Transliterates Japanese (Kanji/Hiragana/Katakana), Korean (Hangul), and Cyrillic
/// text into clean Romanized / Latin alphabet subtitles for song lyrics.
/// Leverages Windows.Globalization.JapanesePhoneticAnalyzer with extensive offline Kanji dictionary fallback.
/// </summary>
public static class RomajiTransliterationService
{
    // Korean Hangul Syllable Components (Revised Romanization of Korean)
    private static readonly string[] HangulInitials =
    [
        "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h"
    ];

    private static readonly string[] HangulMedials =
    [
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i"
    ];

    private static readonly string[] HangulFinals =
    [
        "", "k", "k", "k", "n", "n", "n", "t", "l", "l", "l", "l", "l", "l", "l", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t"
    ];

    // Japanese Digraphs / Combo Kana (Hiragana & Katakana)
    private static readonly (string Kana, string Romaji)[] JapaneseCombos =
    [
        ("きゃ", "kya"), ("きゅ", "kyu"), ("きょ", "kyo"),
        ("しゃ", "sha"), ("しゅ", "shu"), ("しょ", "sho"),
        ("ちゃ", "cha"), ("ちゅ", "chu"), ("ちょ", "cho"),
        ("にゃ", "nya"), ("にゅ", "nyu"), ("にょ", "nyo"),
        ("ひゃ", "hya"), ("ひゅ", "hyu"), ("ひょ", "hyo"),
        ("みゃ", "mya"), ("みゅ", "myu"), ("みょ", "myo"),
        ("りゃ", "rya"), ("りゅ", "ryu"), ("りょ", "ryo"),
        ("ぎゃ", "gya"), ("ぎゅ", "gyu"), ("ぎょ", "gyo"),
        ("じゃ", "ja"), ("じゅ", "ju"), ("じょ", "jo"),
        ("びゃ", "bya"), ("びゅ", "byu"), ("びょ", "byo"),
        ("ぴゃ", "pya"), ("ぴゅ", "pyu"), ("ぴょ", "pyo"),
        // Katakana combos
        ("キャ", "kya"), ("キュ", "kyu"), ("キョ", "kyo"),
        ("シャ", "sha"), ("シュ", "shu"), ("ショ", "sho"),
        ("チャ", "cha"), ("チュ", "chu"), ("チョ", "cho"),
        ("ニャ", "nya"), ("ニュ", "nyu"), ("ニョ", "nyo"),
        ("ヒャ", "hya"), ("ヒュ", "hyu"), ("ヒョ", "hyo"),
        ("ミャ", "mya"), ("ミュ", "myu"), ("ミョ", "myo"),
        ("リャ", "rya"), ("リュ", "ryu"), ("リョ", "ryo"),
        ("ギャ", "gya"), ("ギュ", "gyu"), ("ギョ", "gyo"),
        ("ジャ", "ja"), ("ジュ", "ju"), ("ジョ", "jo"),
        ("ビャ", "bya"), ("ビュ", "byu"), ("ビョ", "byo"),
        ("ピャ", "pya"), ("ピュ", "pyu"), ("ピョ", "pyo"),
        ("ティ", "ti"), ("ディ", "di"), ("トゥ", "tu"), ("ドゥ", "du"),
        ("ファ", "fa"), ("フィ", "fi"), ("フェ", "fe"), ("フォ", "fo"),
        ("ウィ", "wi"), ("ウェ", "we"), ("ウォ", "wo"), ("ヴァ", "va"),
        ("ヴィ", "vi"), ("ヴェ", "ve"), ("ヴォ", "vo"),
        ("シェ", "she"), ("ジェ", "je"), ("チェ", "che"),
        ("ツィ", "tsi")
    ];

    // Single Kana mapping (Hiragana & Katakana)
    private static readonly Dictionary<char, string> SingleKanaMap = new()
    {
        // Hiragana
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['を'] = "wo", ['ん'] = "n",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        // Small kana
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo", ['ゎ'] = "wa",

        // Katakana
        ['ア'] = "a", ['イ'] = "i", ['ウ'] = "u", ['エ'] = "e", ['オ'] = "o",
        ['カ'] = "ka", ['キ'] = "ki", ['ク'] = "ku", ['ケ'] = "ke", ['コ'] = "ko",
        ['サ'] = "sa", ['シ'] = "shi", ['ス'] = "su", ['セ'] = "se", ['ソ'] = "so",
        ['タ'] = "ta", ['チ'] = "chi", ['ツ'] = "tsu", ['テ'] = "te", ['ト'] = "to",
        ['ナ'] = "na", ['ニ'] = "ni", ['ヌ'] = "nu", ['ネ'] = "ne", ['ノ'] = "no",
        ['ハ'] = "ha", ['ヒ'] = "hi", ['フ'] = "fu", ['ヘ'] = "he", ['ホ'] = "ho",
        ['マ'] = "ma", ['ミ'] = "mi", ['ム'] = "mu", ['メ'] = "me", ['モ'] = "mo",
        ['ヤ'] = "ya", ['ユ'] = "yu", ['ヨ'] = "yo",
        ['ラ'] = "ra", ['リ'] = "ri", ['ル'] = "ru", ['レ'] = "re", ['ロ'] = "ro",
        ['ワ'] = "wa", ['ヲ'] = "wo", ['ン'] = "n",
        ['ガ'] = "ga", ['ギ'] = "gi", ['グ'] = "gu", ['ゲ'] = "ge", ['ゴ'] = "go",
        ['ザ'] = "za", ['ジ'] = "ji", ['ズ'] = "zu", ['ゼ'] = "ze", ['ゾ'] = "zo",
        ['ダ'] = "da", ['ヂ'] = "ji", ['ヅ'] = "zu", ['デ'] = "de", ['ド'] = "do",
        ['バ'] = "ba", ['ビ'] = "bi", ['ブ'] = "bu", ['ベ'] = "be", ['ボ'] = "bo",
        ['パ'] = "pa", ['ピ'] = "pi", ['プ'] = "pu", ['ペ'] = "pe", ['ポ'] = "po",
        // Small katakana
        ['ァ'] = "a", ['ィ'] = "i", ['ゥ'] = "u", ['ェ'] = "e", ['ォ'] = "o",
        ['ャ'] = "ya", ['ュ'] = "yu", ['ョ'] = "yo", ['ヮ'] = "wa",
        ['ヴ'] = "vu"
    };

    // Cyrillic mapping
    private static readonly Dictionary<char, string> CyrillicMap = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        ['А'] = "A", ['Б'] = "B", ['В'] = "V", ['Г'] = "G", ['Д'] = "D",
        ['Е'] = "E", ['Ё'] = "Yo", ['Ж'] = "Zh", ['З'] = "Z", ['И'] = "I",
        ['Й'] = "Y", ['К'] = "K", ['Л'] = "L", ['М'] = "M", ['N'] = "N",
        ['О'] = "O", ['П'] = "P", ['Р'] = "R", ['С'] = "S", ['Т'] = "T",
        ['У'] = "U", ['Ф'] = "F", ['Х'] = "Kh", ['Ц'] = "Ts", ['Ч'] = "Ch",
        ['Ш'] = "Sh", ['Щ'] = "Shch", ['Ъ'] = "", ['Ы'] = "Y", ['Ь'] = "",
        ['Э'] = "E", ['Ю'] = "Yu", ['Я'] = "Ya"
    };

    // Common lyric Kanji compounds and roots for reliable fallback when Windows language packs are absent
    private static readonly (string Kanji, string Kana)[] CommonKanjiTable =
    [
        ("間違ってない", "まちがってない"),
        ("間違って", "まちがって"),
        ("間違い", "まちがい"),
        ("間違", "まちが"),
        ("本当なんだ", "ほんとうなんだ"),
        ("本当", "ほんとう"),
        ("約束", "やくそく"),
        ("記憶", "きおく"),
        ("運命", "うんめい"),
        ("奇跡", "きせき"),
        ("理由", "りゆう"),
        ("答え", "こたえ"),
        ("世界", "せかい"),
        ("時間", "じかん"),
        ("未来", "みらい"),
        ("過去", "かこ"),
        ("今日", "きょう"),
        ("明日", "あした"),
        ("昨日", "きのう"),
        ("永遠", "えいえん"),
        ("最後", "さいご"),
        ("最初", "さいしょ"),
        ("全部", "ぜんぶ"),
        ("一人", "ひとり"),
        ("二人", "ふたり"),
        ("一緒", "いっしょ"),
        ("言葉", "ことば"),
        ("気持ち", "きもち"),
        ("秘密", "ひみつ"),
        ("魔法", "まほう"),
        ("太陽", "たいよう"),
        ("地球", "ちきゅう"),
        ("涙", "なみだ"),
        ("心", "こころ"),
        ("夢", "ゆめ"),
        ("愛", "あい"),
        ("恋", "こい"),
        ("声", "こえ"),
        ("歌", "うた"),
        ("夜", "よる"),
        ("朝", "あさ"),
        ("空", "そら"),
        ("雨", "あめ"),
        ("雲", "くも"),
        ("海", "うみ"),
        ("花", "はな"),
        ("星", "ほし"),
        ("月", "つき"),
        ("風", "かぜ"),
        ("光", "ひかり"),
        ("影", "かげ"),
        ("道", "みち"),
        ("街", "まち"),
        ("色", "いろ"),
        ("音", "おと"),
        ("僕", "ぼく"),
        ("君", "きみ"),
        ("私", "わたし"),
        ("俺", "おれ"),
        ("誰", "だれ"),
        ("何", "なに"),
        ("今", "いま"),
        ("手", "て"),
        ("目", "め"),
        ("呼ぶ", "よぶ"),
        ("呼", "よ"),
        ("言", "い"),
        ("思", "おも"),
        ("知", "し"),
        ("見", "み"),
        ("聞", "き"),
        ("行", "い"),
        ("来", "き"),
        ("生", "い"),
        ("死", "し"),
        ("笑", "わら"),
        ("泣", "な"),
        ("忘", "わす"),
        ("消", "き"),
        ("待", "ま"),
        ("痛", "いた"),
        ("嫌", "きら"),
        ("好", "す"),
        ("歩", "ある"),
        ("走", "はし"),
        ("飛", "と"),
        ("落ち", "おち"),
        ("落", "お"),
        ("届", "とど"),
        ("触", "ふ"),
        ("抱", "だ"),
        ("信", "しん"),
        ("願", "ねが"),
        ("祈", "いの"),
        ("神", "かみ"),
        ("優", "やさ"),
        ("強", "つよ"),
        ("弱", "よわ"),
        ("遠", "とお"),
        ("近", "ちか"),
        ("高", "たか"),
        ("低", "ひく"),
        ("深", "ふか"),
        ("白", "しろ"),
        ("黒", "くろ"),
        ("青", "あお"),
        ("赤", "あか"),
        ("春", "はる"),
        ("夏", "なつ"),
        ("秋", "あき"),
        ("冬", "ふゆ"),
        ("寂", "さび"),
        ("悲", "かな"),
        ("嬉", "うれ"),
        ("終", "お"),
        ("始", "はじ"),
        ("人", "ひと")
    ];

    /// <summary>
    /// Checks whether the text contains characters from Japanese, Korean, CJK, or Cyrillic scripts.
    /// </summary>
    public static bool HasNonLatinScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (char c in text)
        {
            // Hangul Syllables and Jamo
            if (c >= 0xAC00 && c <= 0xD7A3) return true;
            if (c >= 0x1100 && c <= 0x11FF) return true;
            // Hiragana
            if (c >= 0x3040 && c <= 0x309F) return true;
            // Katakana
            if (c >= 0x30A0 && c <= 0x30FF) return true;
            // CJK Unified Ideographs (Kanji/Hanzi)
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
            // Cyrillic
            if (c >= 0x0400 && c <= 0x04FF) return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a string still contains Japanese Kanji or Kana characters.
    /// </summary>
    public static bool HasJapaneseScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (c >= 0x3040 && c <= 0x309F) return true; // Hiragana
            if (c >= 0x30A0 && c <= 0x30FF) return true; // Katakana
            if (c >= 0x4E00 && c <= 0x9FFF) return true; // Kanji
        }
        return false;
    }

    /// <summary>
    /// Transliterates non-Latin scripts to Latin alphabet (Romaji/Revised Romanization).
    /// Returns null if the text is already Latin/Western or transliteration isn't applicable.
    /// </summary>
    public static string? Transliterate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasNonLatinScript(text))
        {
            return null;
        }

        // 1. If text contains Japanese Kanji or Kana, process via Japanese morphological phonetic pipeline
        if (HasJapaneseScript(text))
        {
            string? japaneseRomaji = TransliterateJapanese(text);
            if (!string.IsNullOrWhiteSpace(japaneseRomaji))
            {
                return japaneseRomaji;
            }
        }

        // 2. Korean Hangul / Cyrillic / Pure Kana general pipeline
        return TransliterateGeneral(text);
    }

    /// <summary>
    /// Transliterates Japanese text using Windows.Globalization.JapanesePhoneticAnalyzer if available,
    /// falling back to embedded lyric Kanji dictionary + Kana transliteration.
    /// </summary>
    private static string? TransliterateJapanese(string text)
    {
        // 1. Try native Windows 10/11 Japanese Phonetic Analyzer WinRT API
        try
        {
            var words = JapanesePhoneticAnalyzer.GetWords(text, monoRuby: false);
            if (words != null && words.Count > 0)
            {
                var sb = new StringBuilder(text.Length * 2);
                foreach (var word in words)
                {
                    string yomi = word.YomiText;
                    if (string.IsNullOrEmpty(yomi))
                    {
                        yomi = word.DisplayText;
                    }

                    string romaji = TransliterateGeneral(yomi) ?? yomi;

                    if (word.IsPhraseStart && sb.Length > 0 && sb[^1] != ' ')
                    {
                        sb.Append(' ');
                    }

                    sb.Append(romaji);
                }

                string candidate = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && !HasJapaneseScript(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // Fall through to dictionary fallback
        }

        // 2. High-precision dictionary fallback for common Kanji lyrics
        string replaced = text;
        foreach (var (kanji, kana) in CommonKanjiTable)
        {
            if (replaced.Contains(kanji, StringComparison.Ordinal))
            {
                replaced = replaced.Replace(kanji, kana, StringComparison.Ordinal);
            }
        }

        string? fallbackRomaji = TransliterateGeneral(replaced);
        if (!string.IsNullOrWhiteSpace(fallbackRomaji) && !fallbackRomaji.Equals(text.Trim(), StringComparison.Ordinal))
        {
            return fallbackRomaji;
        }

        return null;
    }

    /// <summary>
    /// Translates Korean Hangul, Japanese Kana, and Cyrillic character-by-character into Latin alphabet.
    /// </summary>
    private static string? TransliterateGeneral(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        int i = 0;
        char lastVowel = 'a';

        while (i < text.Length)
        {
            char c = text[i];

            // 1. Korean Hangul Syllable (AC00 - D7A3)
            if (c >= 0xAC00 && c <= 0xD7A3)
            {
                int sIndex = c - 0xAC00;
                int initial = sIndex / (21 * 28);
                int medial = (sIndex % (21 * 28)) / 28;
                int final = sIndex % 28;

                sb.Append(HangulInitials[initial]);
                sb.Append(HangulMedials[medial]);
                if (final > 0)
                {
                    sb.Append(HangulFinals[final]);
                }
                i++;
                continue;
            }

            // 2. Japanese combo kana (2-character digraph)
            if (i + 1 < text.Length)
            {
                string pair = text.Substring(i, 2);
                string? matchedCombo = null;
                foreach (var combo in JapaneseCombos)
                {
                    if (pair == combo.Kana)
                    {
                        matchedCombo = combo.Romaji;
                        break;
                    }
                }

                if (matchedCombo != null)
                {
                    sb.Append(matchedCombo);
                    lastVowel = matchedCombo[^1];
                    i += 2;
                    continue;
                }
            }

            // 3. Small tsu (sokuon: っ / ッ) doubles next consonant
            if (c == 'っ' || c == 'ッ')
            {
                if (i + 1 < text.Length && SingleKanaMap.TryGetValue(text[i + 1], out var nextRomaji) && nextRomaji.Length > 0)
                {
                    sb.Append(nextRomaji[0]);
                }
                i++;
                continue;
            }

            // 4. Chouonpu (ー) extends previous vowel
            if (c == 'ー')
            {
                sb.Append(lastVowel);
                i++;
                continue;
            }

            // 5. Single Kana (Hiragana / Katakana)
            if (SingleKanaMap.TryGetValue(c, out var romaji))
            {
                sb.Append(romaji);
                lastVowel = romaji[^1];
                i++;
                continue;
            }

            // 6. Cyrillic
            if (CyrillicMap.TryGetValue(c, out var cyr))
            {
                sb.Append(cyr);
                i++;
                continue;
            }

            // 7. Regular characters, whitespace, punctuation
            sb.Append(c);
            i++;
        }

        string result = sb.ToString().Trim();

        // If the resulting string is essentially identical to input, return null
        if (result.Equals(text.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return result;
    }
}
