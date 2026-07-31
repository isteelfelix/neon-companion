using System.Collections.Generic;

namespace NeonCompanion.Runtime.Avatar3D
{
    /// <summary>
    /// Maps an emoji code point to one of the avatar emotion names understood by
    /// <c>SetEmotion</c>. Used to read the model's emotional cues out of the visible
    /// response text in real time — emojis double as both an explicit, prompt-guided
    /// marker and a natural fallback (weaker models sprinkle them anyway).
    /// </summary>
    internal static class EmojiEmotion
    {
        private static readonly Dictionary<int, string> Map = Build();

        internal static bool TryMap(int codePoint, out string emotion)
        {
            return Map.TryGetValue(codePoint, out emotion);
        }

        private static Dictionary<int, string> Build()
        {
            var m = new Dictionary<int, string>();

            void Add(string emotion, params int[] codePoints)
            {
                for (int i = 0; i < codePoints.Length; i++)
                    m[codePoints[i]] = emotion;
            }

            // 😀 😃 😄 😁 😊 🙂 😍 🥰 ☺ 😋 😸
            Add("happy", 0x1F600, 0x1F603, 0x1F604, 0x1F601, 0x1F60A, 0x1F642,
                0x1F60D, 0x1F970, 0x263A, 0x1F60B, 0x1F638);
            // 🤩 🎉 😆 🥳 🤗 😹 🙌
            Add("excited", 0x1F929, 0x1F389, 0x1F606, 0x1F973, 0x1F917, 0x1F639, 0x1F64C);
            // 😢 😭 😔 😞 🙁 😟 😥 🥺 😿
            Add("sad", 0x1F622, 0x1F62D, 0x1F614, 0x1F61E, 0x1F641, 0x1F61F,
                0x1F625, 0x1F97A, 0x1F63F);
            // 😠 😡 🤬 👿 😾
            Add("angry", 0x1F620, 0x1F621, 0x1F92C, 0x1F47F, 0x1F63E);
            // 😮 😲 😯 😱 🤯 😳
            Add("surprised", 0x1F62E, 0x1F632, 0x1F62F, 0x1F631, 0x1F92F, 0x1F633);
            // 😕 🤔 😧 🙄 🫤
            Add("confused", 0x1F615, 0x1F914, 0x1F627, 0x1F644, 0x1FAE4);
            // 😌 😇 😎
            Add("relaxed", 0x1F60C, 0x1F607, 0x1F60E);
            // 😴 😪 🥱
            Add("sleepy", 0x1F634, 0x1F62A, 0x1F971);

            return m;
        }
    }
}
