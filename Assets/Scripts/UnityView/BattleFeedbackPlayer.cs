using ProjectHero.Logic.Events;
using UnityEngine;

namespace ProjectHero.UnityView
{
    /// <summary>Local sound, bounce and camera policy. All timing here is purely visual.</summary>
    public sealed class BattleFeedbackPlayer : MonoBehaviour
    {
        [SerializeField] private BattleViewRegistry _registry;
        [SerializeField] private Camera _camera;
        [SerializeField] private AudioSource _audio;
        private AudioClip _hit, _defense, _end;
        private bool _bound;
        private float _shake;
        private Vector3 _previousOffset;
        public int EndFeedbackCount { get; private set; }
        public void Configure(BattleViewRegistry registry, Camera camera, AudioSource audio)
        { _registry = registry; _camera = camera; _audio = audio; }
        public void Bind()
        {
            EndFeedbackCount = 0; _shake = 0;
            if (_bound) return;
            _registry.Feedback.FeedbackRequested += Play; _bound = true;
            if (_hit == null) _hit = Tone("HitFeedback", 150, .045f);
            if (_defense == null) _defense = Tone("DefenseFeedback", 480, .06f);
            if (_end == null) _end = Tone("BattleEndFeedback", 720, .12f);
        }
        public void Unbind()
        {
            if (!_bound) return;
            if (_registry != null) _registry.Feedback.FeedbackRequested -= Play;
            _bound = false;
            if (_camera != null) _camera.transform.position -= _previousOffset;
            _previousOffset = Vector3.zero; _shake = 0;
            if (_audio != null) _audio.Stop();
        }
        private static AudioClip Tone(string name, float frequency, float duration)
        {
            const int rate = 22050; int count = Mathf.CeilToInt(rate * duration);
            var clip = AudioClip.Create(name, count, 1, rate, false); var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = Mathf.Sin(i * frequency * 2 * Mathf.PI / rate) * .08f * (1f - (float)i / count);
            clip.SetData(samples, 0); return clip;
        }
        private void Play(CombatFeedbackCue cue)
        {
            if (cue.Kind == "BattleEnded")
            { if (EndFeedbackCount++ == 0 && _audio != null) _audio.PlayOneShot(_end); return; }
            bool impact = cue.Fact is DamageChannelResolvedEvent damage && damage.AfterBlockDamageQ10 > 0;
            bool defense = cue.Kind == "DodgeSuccess" || cue.Kind == "BlockResolved" || cue.Kind == "Guard" || cue.Kind == "Clash";
            if (_registry.TryGetView(cue.UnitId, out var view)) view.PlayFeedback(cue.Kind);
            if (_audio != null && (impact || defense)) _audio.PlayOneShot(impact ? _hit : _defense);
            if (impact || cue.Kind == "Clash") _shake = .08f;
        }
        private void LateUpdate()
        {
            if (!_bound || _camera == null) return;
            _camera.transform.position -= _previousOffset;
            _shake = Mathf.Max(0, _shake - Time.unscaledDeltaTime);
            _previousOffset = _shake > 0 ? new Vector3(Mathf.Sin(Time.unscaledTime * 93), 0, Mathf.Cos(Time.unscaledTime * 79)) * _shake : Vector3.zero;
            _camera.transform.position += _previousOffset;
        }
        private void OnDestroy()
        {
            Unbind();
            if (_hit != null) Destroy(_hit); if (_defense != null) Destroy(_defense); if (_end != null) Destroy(_end);
        }
    }
}
