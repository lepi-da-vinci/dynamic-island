using System.Text;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Transliterates Japanese (Hiragana/Katakana), Korean (Hangul), and Cyrillic
/// text into clean Romanized / Latin alphabet subtitles for song lyrics.
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

    // Japanese Digraphs / Combo Kana
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
        ("ヴィ", "vi"), ("ヴェ", "ve"), ("ヴォ", "vo")
    ];

    // Single Kana mapping
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
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo",

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
        ['ャ'] = "ya", ['ュ'] = "yu", ['ョ'] = "yo",
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
        ['Й'] = "Y", ['К'] = "K", ['Л'] = "L", ['М'] = "M", ['Н'] = "N",
        ['О'] = "O", ['П'] = "P", ['Р'] = "R", ['С'] = "S", ['Т'] = "T",
        ['У'] = "U", ['Ф'] = "F", ['Х'] = "Kh", ['Ц'] = "Ts", ['Ч'] = "Ch",
        ['Ш'] = "Sh", ['Щ'] = "Shch", ['Ъ'] = "", ['Ы'] = "Y", ['Ь'] = "",
        ['Э'] = "E", ['Ю'] = "Yu", ['Я'] = "Ya"
    };

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
    /// Transliterates non-Latin scripts to Latin alphabet (Romaji/Revised Romanization).
    /// Returns null if the text is already Latin/Western or transliteration isn't applicable.
    /// </summary>
    public static string? Transliterate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasNonLatinScript(text))
        {
            return null;
        }

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

            // 2. Japanese combo kana (2-character digram)
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

            // 5. Single Kana
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

            // 7. Regular characters, whitespace, punctuation, CJK Ideographs
            sb.Append(c);
            i++;
        }

        string result = sb.ToString().Trim();

        // If the resulting string is essentially identical to input (e.g. only Kanji with no Kana/Hangul), return null
        if (result.Equals(text.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return result;
    }
}
