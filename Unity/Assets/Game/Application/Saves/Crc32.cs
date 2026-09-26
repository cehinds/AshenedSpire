using System.Text;
using Ashen.Generated;

namespace Ashen.App.Saves
{
    /// <summary>CRC-32 (IEEE 802.3, reflected) for command-log records (PF-06).</summary>
    public static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[Crc32Algorithm.TableSize];
            for (uint i = 0; i < Crc32Algorithm.TableSize; i++)
            {
                var c = i;
                for (var k = 0; k < Crc32Algorithm.BitsPerByte; k++) c = (c & 1u) != 0 ? Crc32Algorithm.Polynomial ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Of(string text)
        {
            var crc = Crc32Algorithm.Init;
            foreach (var b in Encoding.UTF8.GetBytes(text)) crc = Table[(crc ^ b) & Crc32Algorithm.ByteMask] ^ (crc >> (int)Crc32Algorithm.BitsPerByte);
            return crc ^ Crc32Algorithm.Init;
        }
    }
}
