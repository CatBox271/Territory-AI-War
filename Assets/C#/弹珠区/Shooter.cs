using UnityEngine;

public class Shooter : MonoBehaviour
{
    public void Launch(Rigidbody2D rb)
    {
        if (rb == null) return;
        rb.velocity = (Vector2)transform.up * MapConfig.Instance.marbleSpeed;
    }
}
