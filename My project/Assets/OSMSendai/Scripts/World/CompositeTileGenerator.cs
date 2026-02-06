using System.Threading;
using System.Threading.Tasks;

namespace OsmSendai.World
{
    public sealed class CompositeTileGenerator : ITileGenerator
    {
        private readonly ITileGenerator _primary;
        private readonly ITileGenerator _fallback;

        public CompositeTileGenerator(ITileGenerator primary, ITileGenerator fallback)
        {
            _primary = primary;
            _fallback = fallback;
        }

        public async Task<TileBuildResult> BuildAsync(TileBuildRequest request, CancellationToken cancellationToken)
        {
            var primary = await _primary.BuildAsync(request, cancellationToken);
            if (primary != null)
            {
                return primary;
            }

            return await _fallback.BuildAsync(request, cancellationToken);
        }
    }
}
