using UnityEngine;

public class RotableFilter : MonoBehaviour
{
    public float targetAngle = 0f;
    public float targetMass = 1f;
    public float elasticity = 10f;

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
                rb.gravityScale = MarbleManager.Instance.gravity;
        }
    }

    void FixedUpdate()
    {
        if (rb == null) return;

        float error = Mathf.DeltaAngle(rb.rotation, targetAngle);
        float torque = (elasticity * error - rb.angularVelocity * elasticity * 0.1f) * rb.mass;
        float maxTorque = targetMass * 2f;
        torque = Mathf.Clamp(torque, -maxTorque, maxTorque);
        rb.AddTorque(torque);
    }
}
