using UnityEngine;

public class Shooter : MonoBehaviour
{
    public float v = 10f;

    public void Launch(Rigidbody2D rb)
    {
        if (rb == null) return;
        rb.velocity = (Vector2)transform.up * v;
    }
}
