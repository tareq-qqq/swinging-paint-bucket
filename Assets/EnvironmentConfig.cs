using UnityEngine;

// =====================================================================================
//  EnvironmentConfig — ONE shared source for the environment inputs the whole sim uses.
// =====================================================================================
//  The assignment lists the environment as adjustable inputs (gravity, air resistance,
//  humidity, friction). Rather than entering them separately on the pendulum, the rope
//  and the paint, they all read from this single component, so you set each value ONCE.
//
//  Usage: drop ONE EnvironmentConfig on a GameObject in the scene. Every system finds it
//  via the static Instance. If none exists, each system falls back to its own serialized
//  default, so the scripts still run standalone.
//
//  No Unity physics — these are just shared numbers the hand-written solvers read.
// =====================================================================================
public class EnvironmentConfig : MonoBehaviour
{
    [Tooltip(
        "Gravity value (m/s²), applied straight down. Shared by the bucket pendulum, the rope, and the paint."
    )]
    public float gravity = 9.81f;

    [Tooltip(
        "Air resistance — this ONE value is the pendulum's swing damping AND the paint's air drag (air slowing the air-exposed paint)."
    )]
    public float airResistance = 0.08f;

    [Tooltip("Ambient air temperature (°C). Warmer = thinner paint and faster drying.")]
    public float ambientTemperature = 20f;

    [Range(0f, 1f)]
    [Tooltip("Air humidity (0–1). Humid air dries the exposed paint slowly; dry air fast.")]
    public float humidity = 0.5f;

    [Tooltip("Wind force on air-exposed paint (world space).")]
    public Vector3 wind = Vector3.zero;

    [Range(0f, 1f)]
    [Tooltip("Surface friction (used by the canvas/bucket-wall contact in the surfaces phase).")]
    public float friction = 0.5f;

    // Gravity as a downward vector, for the solvers that want a Vector3.
    public Vector3 GravityVector => new Vector3(0f, -gravity, 0f);

    // Single live instance the other systems read from (last enabled wins).
    public static EnvironmentConfig Instance { get; private set; }

    void OnEnable() => Instance = this;

    void OnDisable()
    {
        if (Instance == this)
            Instance = null;
    }
}
