using Quantum;
using UnityEngine;
using UnityEngine.UI;

/// HUD parry icon: solid when parry is ready, see-through while the parry window is active,
/// then a clockwise radial wipe paints it back to solid over the cooldown.
public class ParryCooldownUI : QuantumSceneViewComponent
{
    [Tooltip("Faded copy of the icon, always visible underneath.")]
    [SerializeField] private Image ghostIcon;
    [Tooltip("Solid copy of the icon; Image Type = Filled, Radial 360, origin Top, clockwise.")]
    [SerializeField] private Image fillIcon;
    [SerializeField] private CanvasGroup group;
    [SerializeField, Range(0f, 1f)] private float ghostAlpha = 0.28f;

    [Header("Ready pop")]
    [SerializeField] private float readyPopScale = 1.15f;
    [SerializeField] private float readyPopSpeed = 6f;

    private float _lastFill = 1f;
    private float _pop;

    public override void OnActivate(Frame frame)
    {
        if (ghostIcon != null)
        {
            var c = ghostIcon.color;
            c.a = ghostAlpha;
            ghostIcon.color = c;
        }
        SetFill(1f);
    }

    public override void OnUpdateView()
    {
        var frame = PredictedFrame;
        if (frame == null) return;

        bool found = TryGetLocalParry(frame, out var parry, out bool isDead);
        if (group != null) group.alpha = found && !isDead ? 1f : 0f;
        if (!found) return;

        float fill;
        if (parry.IsParrying)
            fill = 0f;
        else if (parry.CooldownTimer > 0 && parry.CooldownDuration > 0)
            fill = 1f - Mathf.Clamp01(parry.CooldownTimer.AsFloat / parry.CooldownDuration.AsFloat);
        else
            fill = 1f;

        // Small pop when the cooldown finishes so "ready" is noticeable without looking at the icon
        if (fill >= 1f && _lastFill < 1f) _pop = 1f;
        _lastFill = fill;
        _pop = Mathf.MoveTowards(_pop, 0f, readyPopSpeed * Time.deltaTime);
        transform.localScale = Vector3.one * Mathf.Lerp(1f, readyPopScale, _pop);

        SetFill(fill);
    }

    private bool TryGetLocalParry(Frame frame, out Parry parry, out bool isDead)
    {
        foreach (var pair in frame.GetComponentIterator<PlayerLink>())
        {
            if (!Game.PlayerIsLocal(pair.Component.Player)) continue;
            if (!frame.TryGet(pair.Entity, out parry)) continue;
            isDead = pair.Component.IsDead;
            return true;
        }

        parry = default;
        isDead = false;
        return false;
    }

    private void SetFill(float fill)
    {
        if (fillIcon != null) fillIcon.fillAmount = fill;
    }
}
