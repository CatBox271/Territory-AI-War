using UnityEngine;

public class SlowSmall : MonoBehaviour
{
    public SpriteRenderer sp;
    public float StartA;
    public float DieSpeed;
    public Vector3 velocity;
    public float slow;
    int k;
    float size = 1;
    Color c;

    public void SetVelocity(Vector3 vel,Color col, float size,float DieSpeed)
    {
        this.size = size;
        velocity = vel;
        c = col;
        this.DieSpeed = DieSpeed;
    }
    private void Start()
    {
        c.a = StartA;
        sp.color = c;
        k = Random.Range(0, 2) == 0 ? 1 : -1;
        transform.localEulerAngles = new Vector3(0, 0, Random.Range(0f, 360f));
    }
    float M = 1;
    private void Update()
    {
        transform.localEulerAngles += new Vector3(0, 0, 5f);
        if (M > 0)
        {
            transform.position += velocity * k * Time.deltaTime;
            M = velocity.magnitude - slow * Time.deltaTime;
            if (M > 0) velocity = M * velocity.normalized;
        }
        c.a -= DieSpeed * Time.deltaTime;
        if (c.a <= 0) Destroy(gameObject);
        sp.color = c;
        transform.localScale = Vector3.one * c.a * size;
    }
}
