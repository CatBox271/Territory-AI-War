using UnityEngine;

public class RotableFilter : MonoBehaviour
{
    public float targetAngle = 0f;
    public float targetMass = 1f;
    public float elasticity = 10f;
    public float maxLimit = 2f;

    public AnimationCurve ForceCurve;
    public AnimationCurve Time2Mass;
    private Rigidbody2D rb;
    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void Start()
    {
        if (rb != null)
        {
            rb.mass = targetMass;
            if (MarbleManager.Instance != null)
            {
                rb.gravityScale = MarbleManager.Instance.gravity;
            }
        }
    }

    void FixedUpdate()
    {
        if (rb == null) return;

        targetMass = Time2Mass.Evaluate(Time.time / 60f);

        float angle = Mathf.DeltaAngle(rb.rotation, targetAngle);
        float maxTorque = targetMass * maxLimit;
        float torque = (ForceCurve.Evaluate(angle) - rb.angularVelocity * elasticity) * rb.mass;
        torque = Mathf.Clamp(torque, -maxTorque, maxTorque);
        rb.AddTorque(torque);
    }
}
