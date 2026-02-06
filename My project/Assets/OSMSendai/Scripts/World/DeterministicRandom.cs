namespace OsmSendai.World
{
    internal struct DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public uint NextU32()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        public float Next01()
        {
            return (NextU32() & 0x00FFFFFFu) / 16777216f;
        }

        public float Range(float min, float max)
        {
            return min + (max - min) * Next01();
        }
    }
}

