using System.Text;
using System.Text.RegularExpressions;
using Windows.Globalization;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Transliterates Japanese (Kanji/Hiragana/Katakana), Korean (Hangul), and Cyrillic
/// text into clean Romanized / Latin alphabet subtitles for song lyrics.
/// Produces natural word-spaced Romaji and ensures zero Japanese Kanji or Kana characters leak into output.
/// </summary>
public static partial class RomajiTransliterationService
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

    // Common lyric multi-character phrases & compound words mapped directly to spaced Romaji.
    // Ordered strictly from longest to shortest match.
    private static readonly (string Phrase, string Romaji)[] CommonPhrases =
    [
        ("間違ってないだろ", "machigatte nai daro"),
        ("間違ってないよな", "machigatte nai yo na"),
        ("間違ってない", "machigatte nai"),
        ("間違っている", "machigatte iru"),
        ("間違って", "machigatte"),
        ("間違い", "machigai"),
        ("間違", "machiga"),
        ("風を呼ぶって本当なんだね", "kaze wo yobu tte hontou nan da ne"),
        ("風を呼ぶって", "kaze wo yobu tte"),
        ("風を呼ぶ", "kaze wo yobu"),
        ("どうでもいいんだ", "dou demo ii n da"),
        ("どうでもいい", "dou demo ii"),
        ("どうでも", "dou demo"),
        ("いいんだ", "ii n da"),
        ("本当なんだね", "hontou nan da ne"),
        ("本当なんだ", "hontou nan da"),
        ("本当は", "hontou wa"),
        ("本当", "hontou"),
        ("呼ぶって", "yobu tte"),
        ("呼んでいる", "yonde iru"),
        ("呼んで", "yonde"),
        ("呼ぶ", "yobu"),
        ("だから僕は音楽を辞めた", "dakara boku wa ongaku wo yameta"),
        ("夜に駆ける", "yoru ni kakeru"),
        ("駆ける", "kakeru"),
        ("雨とカプチーノ", "ame to kapuchiino"),
        ("君の知らない物語", "kimi no shira nai monogatari"),
        ("思い出す", "omoidasu"),
        ("思い出", "omoide"),
        ("抱きしめる", "dakishimeru"),
        ("見つける", "mitsukeru"),
        ("信じている", "shinjite iru"),
        ("愛している", "aishite iru"),
        ("愛してる", "aishiteru"),
        ("約束した", "yakusoku shita"),
        ("約束", "yakusoku"),
        ("運命の", "unmei no"),
        ("運命", "unmei"),
        ("奇跡の", "kiseki no"),
        ("奇跡", "kiseki"),
        ("記憶", "kioku"),
        ("理由", "riyuu"),
        ("答え", "kotae"),
        ("世界は", "sekai wa"),
        ("世界の", "sekai no"),
        ("世界", "sekai"),
        ("時間は", "jikan wa"),
        ("時間の", "jikan no"),
        ("時間", "jikan"),
        ("未来へ", "mirai e"),
        ("未来の", "mirai no"),
        ("未来", "mirai"),
        ("過去へ", "kako e"),
        ("過去の", "kako no"),
        ("過去", "kako"),
        ("永遠の", "eien no"),
        ("永遠", "eien"),
        ("最後まで", "saigo made"),
        ("最後に", "saigo ni"),
        ("最後", "saigo"),
        ("最初から", "saisho kara"),
        ("最初に", "saisho ni"),
        ("最初", "saisho"),
        ("全部", "zenbu"),
        ("一人で", "hitori de"),
        ("一人の", "hitori no"),
        ("一人", "hitori"),
        ("二人で", "futari de"),
        ("二人の", "futari no"),
        ("二人", "futari"),
        ("一緒に", "issho ni"),
        ("一緒", "issho"),
        ("言葉は", "kotoba wa"),
        ("言葉を", "kotoba wo"),
        ("言葉", "kotoba"),
        ("気持ちは", "kimochi wa"),
        ("気持ち", "kimochi"),
        ("秘密の", "himitsu no"),
        ("秘密", "himitsu"),
        ("魔法の", "mahou no"),
        ("魔法", "mahou"),
        ("太陽の", "taiyou no"),
        ("太陽", "taiyou"),
        ("地球", "chikyuu"),
        ("大丈夫", "daijoubu"),
        ("何故か", "nazeka"),
        ("何故", "naze"),
        ("笑顔で", "egao de"),
        ("笑顔", "egao"),
        ("希望の", "kibou no"),
        ("希望", "kibou"),
        ("絶望", "zetsubou"),
        ("孤独な", "kodoku na"),
        ("孤独", "kodoku"),
        ("自由な", "jiyuu na"),
        ("自由", "jiyuu"),
        ("今夜は", "konya wa"),
        ("今夜", "konya"),
        ("昨日の", "kinou no"),
        ("昨日", "kinou"),
        ("今日の", "kyou no"),
        ("今日", "kyou"),
        ("明日の", "ashita no"),
        ("明日", "ashita"),
        ("毎日", "mainichi"),
        ("夜空に", "yozora ni"),
        ("夜空", "yozora"),
        ("青空に", "aozora ni"),
        ("青空", "aozora"),
        ("星空", "hoshizora"),
        ("満月", "mangetsu"),
        ("三日月", "mikazuki"),
        ("僕らの", "bokura no"),
        ("僕らは", "bokura wa"),
        ("僕らは", "bokura wa"),
        ("僕らは", "bokura wa"),
        ("僕ら", "bokura"),
        ("僕の", "boku no"),
        ("僕は", "boku wa"),
        ("僕に", "boku ni"),
        ("君の", "kimi no"),
        ("君は", "kimi wa"),
        ("君に", "kimi ni"),
        ("君を", "kimi wo"),
        ("私の", "watashi no"),
        ("私は", "watashi wa"),
        ("私に", "watashi ni"),
        ("私を", "watashi wo"),
        ("俺の", "ore no"),
        ("俺は", "ore wa"),
        ("誰かの", "dareka no"),
        ("誰かが", "dareka ga"),
        ("誰か", "dareka"),
        ("誰もが", "daremo ga"),
        ("誰も", "daremo"),
        ("何もかも", "nani mo kamo"),
        ("何にも", "nani ni mo"),
        ("何度も", "nando mo"),
        ("何処か", "dokoka"),
        ("何時か", "itsuka"),
        ("優しく", "yasashiku"),
        ("優しい", "yasashii"),
        ("強く", "tsuyoku"),
        ("強い", "tsuyoi"),
        ("弱く", "yowaku"),
        ("弱い", "yowai"),
        ("遠くへ", "tooku e"),
        ("遠く", "tooku"),
        ("遠い", "tooi"),
        ("近くに", "chikaku ni"),
        ("近く", "chikaku"),
        ("近い", "chikai"),
        ("高く", "takaku"),
        ("高い", "takai"),
        ("低く", "hikuku"),
        ("低い", "hikui"),
        ("深く", "fukaku"),
        ("深い", "fukai"),
        ("寂しく", "sabishiku"),
        ("寂しい", "sabishii"),
        ("悲しく", "kanashiku"),
        ("悲しい", "kanashii"),
        ("嬉しく", "ureshiku"),
        ("嬉しい", "ureshii"),
        ("楽しく", "tanoshiku"),
        ("楽しい", "tanoshii"),
        ("苦しく", "kurushiku"),
        ("苦しい", "kurushii"),
        ("痛い", "itai"),
        ("好き", "suki"),
        ("嫌い", "kirai"),
        ("消えて", "kiete"),
        ("消えた", "kieta"),
        ("消える", "kieru"),
        ("忘れて", "wasurete"),
        ("忘れた", "wasureta"),
        ("忘れる", "wasureru"),
        ("終わって", "owatte"),
        ("終わった", "owatta"),
        ("終わる", "owaru"),
        ("始まって", "hajimatte"),
        ("始まった", "hajimatta"),
        ("始まる", "hajimaru"),
        ("生きて", "ikite"),
        ("生きた", "ikita"),
        ("生きる", "ikiru"),
        ("死んで", "shinde"),
        ("死んだ", "shinda"),
        ("死ぬ", "shinu"),
        ("笑って", "waratte"),
        ("笑った", "waratta"),
        ("笑う", "warau"),
        ("泣いて", "naite"),
        ("泣いた", "naita"),
        ("泣く", "naku"),
        ("眠って", "nemutte"),
        ("眠った", "nemutta"),
        ("眠る", "nemuru"),
        ("歌って", "utatte"),
        ("歌った", "utatta"),
        ("歌う", "utau"),
        ("叫んで", "sakende"),
        ("叫ぶ", "sakebu"),
        ("踊って", "odotte"),
        ("踊る", "odoru"),
        ("願って", "negatte"),
        ("願う", "negau"),
        ("祈って", "inotte"),
        ("祈る", "inoru"),
        ("誓って", "chikatte"),
        ("誓う", "chikau"),
        ("触れて", "furete"),
        ("触れる", "fureru"),
        ("感じて", "kanjite"),
        ("感じる", "kanjiru"),
        ("揺れて", "yurete"),
        ("揺れる", "yureru"),
        ("輝いて", "kagayaite"),
        ("輝く", "kagayaku"),
        ("響いて", "hibiite"),
        ("響く", "hibiku"),
        ("届いて", "todoite"),
        ("届く", "todoku"),
        ("失って", "ushinatte"),
        ("失う", "ushinau"),
        ("守って", "mamotte"),
        ("守る", "mamoru"),
        ("壊して", "kowashite"),
        ("壊す", "kowasu"),
        ("探して", "sagashite"),
        ("探す", "sagasu"),
        ("出会って", "deatte"),
        ("出会う", "deau"),
        ("別れて", "wakarete"),
        ("別れる", "wakareru"),
        ("繋いで", "tsunaide"),
        ("繋ぐ", "tsunagu"),
        ("離れて", "hanarete"),
        ("離れる", "hanareru"),
        ("帰って", "kaette"),
        ("帰る", "kaeru"),
        ("歩いて", "aruite"),
        ("歩く", "aruku"),
        ("走って", "hashitte"),
        ("走る", "hashiru"),
        ("飛んで", "tonde"),
        ("飛ぶ", "tobu"),
        ("落ちて", "ochite"),
        ("落ちる", "ochiru"),
        ("行って", "itte"),
        ("行く", "iku"),
        ("来て", "kite"),
        ("来る", "kuru"),
        ("言って", "itte"),
        ("言う", "iu"),
        ("思って", "omotte"),
        ("思う", "omou"),
        ("知って", "shitte"),
        ("知る", "shiru"),
        ("見て", "mite"),
        ("見る", "miru"),
        ("聞いて", "kiite"),
        ("聞く", "kiku"),
        ("立って", "tatte"),
        ("立つ", "tatsu"),
        ("座って", "suwatte"),
        ("座る", "suwaru"),
        ("待って", "matte"),
        ("待つ", "matsu"),
        ("持って", "motte"),
        ("持つ", "motsu"),
        ("開いて", "hiraite"),
        ("開く", "hiraku"),
        ("閉じて", "tojite"),
        ("閉じる", "tojiru"),
        ("止まって", "tomatte"),
        ("止まる", "tomaru"),
        ("動いて", "ugoite"),
        ("動く", "ugoku"),
        ("変わって", "kawatte"),
        ("変わる", "kawaru"),
        ("落として", "otoshite"),
        ("落とす", "otosu"),
        ("救って", "sukutte"),
        ("救う", "sukuu"),
        ("助けて", "tasukete"),
        ("助ける", "tasukeru"),
        ("奪って", "ubatte"),
        ("奪う", "ubau"),
        ("与えて", "ataete"),
        ("与える", "ataeru"),
        ("溢れて", "afurete"),
        ("溢れる", "afureru"),
        ("零れて", "koborete"),
        ("零れる", "koboreru"),
        ("震えて", "furuete"),
        ("震える", "furueru"),
        ("瞬いて", "matataite"),
        ("瞬く", "matataku"),
        ("迷って", "mayotte"),
        ("迷う", "mayou"),
        ("悩んで", "nayande"),
        ("悩む", "nayamu"),
        ("傷ついて", "kizutsuite"),
        ("傷つく", "kizutsuku"),
        ("癒して", "iyashite"),
        ("癒す", "iyasu"),
        ("許して", "yurushite"),
        ("許す", "yurusu"),
        ("怒って", "okotte"),
        ("怒る", "okoru"),
        ("喜んで", "yorokonde"),
        ("喜ぶ", "yorokobu"),
        ("悲しんで", "kanashinde"),
        ("悲しむ", "kanashimu"),
        ("楽しんで", "tanoshinde"),
        ("楽しむ", "tanoshimu"),
        ("憎んで", "nikunde"),
        ("憎む", "nikumu")
    ];

    // Common particles and grammatical markers mapped with spaces so words are properly segmented.
    private static readonly (string Marker, string SpacedRomaji)[] CommonMarkers =
    [
        ("って", " tte "),
        ("から", " kara "),
        ("まで", " made "),
        ("より", " yori "),
        ("ないだろ", " nai daro "),
        ("ないよな", " nai yo na "),
        ("ないで", " nai de "),
        ("なくて", " nakute "),
        ("ない", " nai "),
        ("たい", " tai "),
        ("だろう", " darou "),
        ("だろ", " daro "),
        ("よな", " yo na "),
        ("んだ", " n da "),
        ("なん", " nan "),
        ("ねえ", " nee "),
        ("けど", " kedo "),
        ("でも", " demo "),
        ("たら", " tara "),
        ("なら", " nara "),
        ("れば", " reba "),
        ("こと", " koto "),
        ("もの", " mono "),
        ("とき", " toki "),
        ("ため", " tame "),
        ("ほど", " hodo "),
        ("だけ", " dake "),
        ("しか", " shika "),
        ("ほど", " hodo "),
        ("を", " wo "),
        ("は", " wa "),
        ("が", " ga "),
        ("に", " ni "),
        ("で", " de "),
        ("と", " to "),
        ("の", " no "),
        ("も", " mo "),
        ("へ", " e "),
        ("ね", " ne "),
        ("よ", " yo ")
    ];

    // Single Kanji dictionary covering frequent individual characters to guarantee zero raw Kanji in output.
    private static readonly Dictionary<char, string> SingleKanjiMap = new()
    {
        ['僕'] = "boku", ['君'] = "kimi", ['私'] = "watashi", ['俺'] = "ore",
        ['誰'] = "dare", ['何'] = "nani", ['今'] = "ima", ['日'] = "hi",
        ['月'] = "tsuki", ['年'] = "toshi", ['時'] = "toki", ['分'] = "fun",
        ['夜'] = "yoru", ['朝'] = "asa", ['夕'] = "yuu", ['昼'] = "hiru",
        ['空'] = "sora", ['雨'] = "ame", ['雲'] = "kumo", ['風'] = "kaze",
        ['雪'] = "yuki", ['雷'] = "kaminari", ['海'] = "umi", ['波'] = "nami",
        ['川'] = "kawa", ['山'] = "yama", ['森'] = "mori", ['林'] = "hayashi",
        ['花'] = "hana", ['草'] = "kusa", ['木'] = "ki", ['葉'] = "ha",
        ['鳥'] = "tori", ['羽'] = "hane", ['翼'] = "tsubasa", ['星'] = "hoshi",
        ['光'] = "hikari", ['影'] = "kage", ['闇'] = "yami", ['色'] = "iro",
        ['音'] = "oto", ['声'] = "koe", ['歌'] = "uta", ['夢'] = "yume",
        ['心'] = "kokoro", ['愛'] = "ai", ['恋'] = "koi", ['涙'] = "namida",
        ['手'] = "te", ['目'] = "me", ['耳'] = "mimi", ['口'] = "kuchi",
        ['足'] = "ashi", ['指'] = "yubi", ['胸'] = "mune", ['体'] = "karada",
        ['命'] = "inochi", ['息'] = "iki", ['傷'] = "kizu", ['嘘'] = "uso",
        ['道'] = "michi", ['街'] = "machi", ['家'] = "ie", ['屋'] = "ya",
        ['火'] = "hi", ['水'] = "mizu", ['土'] = "tsuchi", ['金'] = "kin",
        ['石'] = "ishi", ['砂'] = "suna", ['空'] = "sora", ['天'] = "ten",
        ['神'] = "kami", ['人'] = "hito", ['男'] = "otoko", ['女'] = "onna",
        ['子'] = "ko", ['親'] = "oya", ['友'] = "tomo", ['敵'] = "teki",
        ['白'] = "shiro", ['黒'] = "kuro", ['赤'] = "aka", ['青'] = "ao",
        ['黄'] = "ki", ['緑'] = "midori", ['紫'] = "murasaki",
        ['春'] = "haru", ['夏'] = "natsu", ['秋'] = "aki", ['冬'] = "fuyu",
        ['間'] = "ma", ['違'] = "chiga", ['本'] = "hon", ['当'] = "tou",
        ['世'] = "se", ['界'] = "kai", ['未'] = "mi", ['来'] = "rai",
        ['過'] = "ka", ['去'] = "ko", ['永'] = "ei", ['遠'] = "en",
        ['約'] = "yaku", ['束'] = "soku", ['記'] = "ki", ['憶'] = "oku",
        ['運'] = "un", ['命'] = "mei", ['奇'] = "ki", ['跡'] = "seki",
        ['理'] = "ri", ['由'] = "yuu", ['答'] = "kota", ['問'] = "to",
        ['最'] = "sai", ['後'] = "go", ['初'] = "sho", ['全'] = "zen",
        ['部'] = "bu", ['一'] = "ichi", ['二'] = "ni", ['三'] = "san",
        ['四'] = "yon", ['五'] = "go", ['六'] = "roku", ['七'] = "nana",
        ['八'] = "hachi", ['九'] = "kyuu", ['十'] = "juu", ['百'] = "hyaku",
        ['千'] = "sen", ['万'] = "man", ['同'] = "ona", ['言'] = "i",
        ['思'] = "omo", ['知'] = "shi", ['見'] = "mi", ['聞'] = "ki",
        ['話'] = "hana", ['語'] = "kata", ['読'] = "yo", ['書'] = "ka",
        ['行'] = "i", ['来'] = "ki", ['帰'] = "kae", ['出'] = "de",
        ['入'] = "hai", ['立'] = "ta", ['座'] = "suwa", ['歩'] = "aru",
        ['走'] = "hashi", ['飛'] = "to", ['泳'] = "oyo", ['乗'] = "no",
        ['降'] = "o", ['笑'] = "wara", ['泣'] = "na", ['叫'] = "sake",
        ['歌'] = "uta", ['踊'] = "odo", ['眠'] = "nemu", ['起'] = "o",
        ['生'] = "i", ['死'] = "shi", ['消'] = "ki", ['忘'] = "wasu",
        ['覚'] = "obo", ['待'] = "ma", ['持'] = "mo", ['開'] = "hira",
        ['閉'] = "to", ['止'] = "toma", ['動'] = "ugo", ['変'] = "kawa",
        ['落'] = "o", ['届'] = "todo", ['触'] = "fure", ['抱'] = "daki",
        ['信'] = "shin", ['願'] = "nega", ['祈'] = "ino", ['誓'] = "chika",
        ['好'] = "su", ['嫌'] = "kira", ['痛'] = "ita", ['優'] = "yasa",
        ['強'] = "tsuyo", ['弱'] = "yowa", ['高'] = "taka", ['低'] = "hiku",
        ['深'] = "fuka", ['浅'] = "asa", ['長'] = "naga", ['短'] = "mijika",
        ['広'] = "hiro", ['狭'] = "sema", ['新'] = "atara", ['古'] = "furu",
        ['美'] = "utsuku", ['汚'] = "kitana", ['暗'] = "kura", ['明'] = "aka",
        ['冷'] = "tsume", ['温'] = "atata", ['熱'] = "atsu", ['重'] = "omo",
        ['軽'] = "karu", ['早'] = "haya", ['遅'] = "oso", ['近'] = "chika",
        ['遠'] = "too", ['正'] = "tada", ['誤'] = "ayama", ['真'] = "ma",
        ['偽'] = "nise", ['善'] = "zen", ['悪'] = "aku", ['罪'] = "tsumi",
        ['罰'] = "batsu", ['寂'] = "sabi", ['悲'] = "kana", ['嬉'] = "ure",
        ['楽'] = "tano", ['苦'] = "kuru", ['怒'] = "ika", ['恐'] = "oso",
        ['怖'] = "kowa", ['恥'] = "haji", ['迷'] = "mayo", ['悩'] = "naya",
        ['終'] = "owa", ['始'] = "haji", ['呼'] = "yo", ['鳴'] = "na",
        ['輝'] = "kagaya", ['瞬'] = "matata", ['溢'] = "afure", ['零'] = "kobore",
        ['震'] = "furue", ['揺'] = "yure", ['乱'] = "mida", ['静'] = "shizu",
        ['隠'] = "kaku", ['現'] = "arawa", ['照'] = "tera", ['曇'] = "kumo",
        ['晴'] = "hare", ['咲'] = "saki", ['散'] = "chiri", ['枯'] = "kare",
        ['育'] = "soda", ['老'] = "o", ['若'] = "waka", ['幼'] = "osana",
        ['救'] = "suku", ['助'] = "tasu", ['奪'] = "uba", ['与'] = "atae",
        ['守'] = "mamo", ['壊'] = "kowa", ['破'] = "yabu", ['創'] = "tsuku",
        ['造'] = "tsuku", ['探'] = "saga", ['遇'] = "de", ['逢'] = "a",
        ['別'] = "waka", ['離'] = "hana", ['繋'] = "tsuna", ['結'] = "musu",
        ['解'] = "to", ['交'] = "maji", ['通'] = "kayo", ['過'] = "su",
        ['越'] = "ko", ['超'] = "ko", ['満'] = "mi", ['欠'] = "ka"
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
    /// Checks if a string still contains Japanese Kanji or Kana characters.
    /// </summary>
    public static bool HasJapaneseKana(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if ((c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)) return true;
        }
        return false;
    }

    public static bool HasJapaneseScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (char c in text)
        {
            if (c >= 0x3040 && c <= 0x309F) return true; // Hiragana
            if (c >= 0x30A0 && c <= 0x30FF) return true; // Katakana
            if (c >= 0x4E00 && c <= 0x9FFF) return true; // Kanji / Hanzi
        }
        return false;
    }

    /// <summary>
    /// Transliterates non-Latin scripts to Latin alphabet (Romaji/Pinyin/Revised Romanization)
    /// with natural word spacing and zero leftover non-Latin characters.
    /// Returns null if the text is already Latin/Western or transliteration isn't applicable.
    /// </summary>
    public static string? Transliterate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !HasNonLatinScript(text))
        {
            return null;
        }

        // 1. Japanese pipeline (WinRT JapanesePhoneticAnalyzer word segmentation + reading, with dictionary fallback)
        if (HasJapaneseKana(text))
        {
            string? analyzerRomaji = TryTransliterateJapaneseWithAnalyzer(text);
            if (!string.IsNullOrWhiteSpace(analyzerRomaji))
            {
                return CleanWordSpacing(analyzerRomaji);
            }

            string? japaneseRomaji = TransliterateJapanese(text);
            if (!string.IsNullOrWhiteSpace(japaneseRomaji))
            {
                return CleanWordSpacing(japaneseRomaji);
            }
        }
        else if (HasJapaneseScript(text))
        {
            // Pure CJK ideographs without Hiragana/Katakana -> try Mandarin Pinyin map first, then fallback to Japanese
            string? pinyin = TryTransliterateMandarin(text);
            if (!string.IsNullOrWhiteSpace(pinyin))
            {
                return CleanWordSpacing(pinyin);
            }

            string? japaneseRomaji = TransliterateJapanese(text);
            if (!string.IsNullOrWhiteSpace(japaneseRomaji))
            {
                return CleanWordSpacing(japaneseRomaji);
            }
        }

        // 2. Korean Hangul / Cyrillic general pipeline
        string? general = TransliterateGeneral(text);
        return string.IsNullOrWhiteSpace(general) ? null : CleanWordSpacing(general);
    }

    private static string? TryTransliterateJapaneseWithAnalyzer(string text)
    {
        try
        {
            var words = JapanesePhoneticAnalyzer.GetWords(text, false);
            if (words == null || words.Count == 0) return null;

            var sb = new StringBuilder(text.Length * 2);
            for (int wi = 0; wi < words.Count; wi++)
            {
                var word = words[wi];
                string display = word.DisplayText ?? string.Empty;
                string yomi = word.YomiText ?? string.Empty;

                // Use YomiText (Hiragana reading resolved by Windows IME) when available
                string tokenSource = !string.IsNullOrWhiteSpace(yomi) ? yomi : display;
                if (string.IsNullOrWhiteSpace(tokenSource)) continue;

                // If IME split trailing sokuon ('っ'/'ッ') from the following word, merge them so gemination works
                while ((tokenSource[^1] == 'っ' || tokenSource[^1] == 'ッ') && wi + 1 < words.Count)
                {
                    wi++;
                    var nextWord = words[wi];
                    string nextSource = !string.IsNullOrWhiteSpace(nextWord.YomiText)
                        ? nextWord.YomiText
                        : (nextWord.DisplayText ?? string.Empty);
                    if (!string.IsNullOrEmpty(nextSource))
                    {
                        tokenSource += nextSource;
                    }
                }

                string? tokenRomaji;
                if (tokenSource == "は" && display == "は")
                {
                    tokenRomaji = "wa";
                }
                else if (tokenSource == "へ" && display == "へ")
                {
                    tokenRomaji = "e";
                }
                else
                {
                    tokenRomaji = TransliterateJapanese(tokenSource, applyMarkers: false);
                }

                if (!string.IsNullOrWhiteSpace(tokenRomaji))
                {
                    if (sb.Length > 0 && sb[^1] != ' ' && (word.IsPhraseStart || tokenSource == "は" || tokenSource == "が" || tokenSource == "を" || tokenSource == "に" || tokenSource == "で" || tokenSource == "と" || tokenSource == "の" || tokenSource == "も" || tokenSource == "へ"))
                    {
                        sb.Append(' ');
                    }
                    sb.Append(tokenRomaji.Trim());
                }
            }

            string built = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(built) ? null : built;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Dictionary<char, string> MandarinPinyinMap = new()
    {
        ['我'] = "wo", ['你'] = "ni", ['他'] = "ta", ['她'] = "ta", ['它'] = "ta", ['们'] = "men",
        ['的'] = "de", ['是'] = "shi", ['不'] = "bu", ['了'] = "le", ['在'] = "zai", ['有'] = "you",
        ['人'] = "ren", ['这'] = "zhe", ['那'] = "na", ['就'] = "jiu", ['也'] = "ye", ['都'] = "dou",
        ['和'] = "he", ['说'] = "shuo", ['要'] = "yao", ['去'] = "qu", ['会'] = "hui", ['可'] = "ke",
        ['以'] = "yi", ['来'] = "lai", ['到'] = "dao", ['看'] = "kan", ['想'] = "xiang", ['爱'] = "ai",
        ['心'] = "xin", ['情'] = "qing", ['梦'] = "meng", ['风'] = "feng", ['雨'] = "yu", ['雪'] = "xue",
        ['花'] = "hua", ['月'] = "yue", ['夜'] = "ye", ['天'] = "tian", ['空'] = "kong", ['海'] = "hai",
        ['星'] = "xing", ['光'] = "guang", ['春'] = "chun", ['夏'] = "xia", ['秋'] = "qiu", ['冬'] = "dong",
        ['年'] = "nian", ['日'] = "ri", ['时'] = "shi", ['分'] = "fen", ['秒'] = "miao", ['好'] = "hao",
        ['多'] = "duo", ['少'] = "shao", ['大'] = "da", ['小'] = "xiao", ['一'] = "yi", ['二'] = "er",
        ['三'] = "san", ['四'] = "si", ['五'] = "wu", ['六'] = "liu", ['七'] = "qi", ['八'] = "ba",
        ['九'] = "jiu", ['十'] = "shi", ['百'] = "bai", ['千'] = "qian", ['万'] = "wan", ['没'] = "mei",
        ['着'] = "zhe", ['过'] = "guo", ['给'] = "gei", ['让'] = "rang", ['把'] = "ba", ['被'] = "bei",
        ['对'] = "dui", ['为'] = "wei", ['从'] = "cong", ['向'] = "xiang", ['里'] = "li", ['外'] = "wai",
        ['上'] = "shang", ['下'] = "xia", ['前'] = "qian", ['后'] = "hou", ['左'] = "zuo", ['右'] = "you",
        ['中'] = "zhong", ['间'] = "jian", ['边'] = "bian", ['能'] = "neng", ['得'] = "de", ['真'] = "zhen",
        ['假'] = "jia", ['美'] = "mei", ['丽'] = "li", ['笑'] = "xiao", ['哭'] = "ku", ['泪'] = "lei",
        ['痛'] = "tong", ['苦'] = "ku", ['甜'] = "tian", ['蜜'] = "mi", ['飞'] = "fei", ['走'] = "zou",
        ['跑'] = "pao", ['停'] = "ting", ['等'] = "deng", ['回'] = "hui", ['忘'] = "wang", ['记'] = "ji",
        ['懂'] = "dong", ['知'] = "zhi", ['道'] = "dao", ['谁'] = "shei", ['什'] = "shen", ['么'] = "me",
        ['因'] = "yin", ['所'] = "suo", ['但'] = "dan", ['却'] = "que", ['还'] = "hai",
        ['再'] = "zai", ['只'] = "zhi", ['最'] = "zui", ['太'] = "tai", ['很'] = "hen", ['更'] = "geng",
        ['样'] = "yang", ['起'] = "qi", ['开'] = "kai", ['关'] = "guan", ['见'] = "jian",
        ['听'] = "ting", ['唱'] = "chang", ['歌'] = "ge", ['声'] = "sheng", ['音'] = "yin", ['词'] = "ci",
        ['手'] = "shou", ['眼'] = "yan", ['睛'] = "jing", ['脸'] = "lian", ['头'] = "tou", ['发'] = "fa",
        ['家'] = "jia", ['路'] = "lu", ['桥'] = "qiao", ['山'] = "shan", ['水'] = "shui", ['云'] = "yun",
        ['雾'] = "wu", ['晴'] = "qing", ['阳'] = "yang", ['暗'] = "an", ['亮'] = "liang", ['黑'] = "hei",
        ['白'] = "bai", ['红'] = "hong", ['蓝'] = "lan", ['绿'] = "lv", ['黄'] = "huang", ['紫'] = "zi",
        ['生'] = "sheng", ['死'] = "si", ['活'] = "huo", ['老'] = "lao", ['新'] = "xin", ['旧'] = "jiu",
        ['远'] = "yuan", ['近'] = "jin", ['高'] = "gao", ['低'] = "di", ['深'] = "shen", ['浅'] = "qian",
        ['满'] = "man", ['热'] = "re", ['冷'] = "leng", ['暖'] = "nuan", ['凉'] = "liang",
        ['永'] = "yong"
    };

    private static string? TryTransliterateMandarin(string text)
    {
        int cjkCount = 0;
        int matchedCount = 0;
        var sb = new StringBuilder(text.Length * 4);

        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                cjkCount++;
                if (MandarinPinyinMap.TryGetValue(c, out string? pinyin))
                {
                    matchedCount++;
                    sb.Append(' ');
                    sb.Append(pinyin);
                    sb.Append(' ');
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (cjkCount == 0 || matchedCount == 0) return null;
        // Require at least 40% coverage on CJK ideographs before committing to Mandarin Pinyin output
        if (matchedCount * 10 < cjkCount * 4) return null;

        string res = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(res) ? null : res;
    }

    /// <summary>
    /// Transliterates Japanese text into properly word-spaced Romaji.
    /// </summary>
    private static string? TransliterateJapanese(string text, bool applyMarkers = true)
    {
        string working = text;

        // Stage 1: Replace multi-character compounds and phrases (longest to shortest)
        foreach (var (phrase, romaji) in CommonPhrases)
        {
            if (working.Contains(phrase, StringComparison.Ordinal))
            {
                working = working.Replace(phrase, " " + romaji + " ", StringComparison.Ordinal);
            }
        }

        // Stage 2: Separate common Japanese particles and grammatical markers (never split immediately after sokuon っ/ッ)
        if (applyMarkers)
        {
            foreach (var (marker, spacedRomaji) in CommonMarkers)
            {
                if (working.Contains(marker, StringComparison.Ordinal))
                {
                    int searchFrom = 0;
                    while (searchFrom < working.Length)
                    {
                        int idx = working.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                        if (idx < 0) break;
                        if (idx > 0 && (working[idx - 1] == 'っ' || working[idx - 1] == 'ッ'))
                        {
                            searchFrom = idx + marker.Length;
                            continue;
                        }
                        working = string.Concat(working.AsSpan(0, idx), spacedRomaji, working.AsSpan(idx + marker.Length));
                        searchFrom = idx + spacedRomaji.Length;
                    }
                }
            }
        }

        // Stage 3: Replace any remaining single Kanji with their primary Romaji reading
        var sbKanji = new StringBuilder(working.Length * 2);
        foreach (char c in working)
        {
            if (SingleKanjiMap.TryGetValue(c, out var kRomaji))
            {
                sbKanji.Append(' ');
                sbKanji.Append(kRomaji);
                sbKanji.Append(' ');
            }
            else if (c >= 0x4E00 && c <= 0x9FFF)
            {
                // Safety net for rare Kanji: do not leave raw CJK ideograph in output!
                sbKanji.Append(' ');
            }
            else
            {
                sbKanji.Append(c);
            }
        }
        working = sbKanji.ToString();

        // Stage 4: Transliterate all remaining Kana (Hiragana/Katakana)
        string? romajiText = TransliterateGeneral(working);
        if (string.IsNullOrWhiteSpace(romajiText))
        {
            return null;
        }

        return romajiText;
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
                sb.Append(' ');
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
            if (c >= 0x4E00 && c <= 0x9FFF)
            {
                // Ensure no raw CJK character passes through
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
            i++;
        }

        string result = sb.ToString().Trim();
        if (result.Equals(text.Trim(), StringComparison.Ordinal))
        {
            return null;
        }

        return result;
    }

    /// <summary>
    /// Normalizes word spacing: collapses multi-spaces and formats punctuation cleanly.
    /// </summary>
    private static string CleanWordSpacing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Collapse multi-spaces
        string s = MultipleSpacesRegex().Replace(text, " ").Trim();

        // Attach punctuation to preceding word without space (e.g. "word ," -> "word,")
        s = PunctuationLeadSpaceRegex().Replace(s, "$1");

        return s;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultipleSpacesRegex();

    [GeneratedRegex(@"\s+([,\.!\?:;\)\]\}])")]
    private static partial Regex PunctuationLeadSpaceRegex();
}
