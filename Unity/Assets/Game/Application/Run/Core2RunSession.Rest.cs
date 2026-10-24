namespace Ashen.App.Run
{
    public sealed partial class Core2RunSession
    {
        private bool _resting;

        /// <summary>The run is at a rest place (WP-16).</summary>
        public bool IsResting => _resting;
    }
}
