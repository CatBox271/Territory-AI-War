using UnityEngine;

public class AutoRotater : MonoBehaviour
{
    public float speed = 30f;
    public Vector2 angleRange = new Vector2(0f, 360f);
    public bool loop = true;
    public bool active = true;

    private float currentAngle;
    private int direction = 1;

    void Start()
    {
        currentAngle = NormalizeAngle(transform.localEulerAngles.z);
    }

    void Update()
    {
        if (!active) return;

        float min = angleRange.x;
        float max = angleRange.y;

        currentAngle += speed * Time.deltaTime * direction;

        if (loop)
        {
            float range = max - min;
            if (range <= 0f) return;
            while (currentAngle > max) currentAngle -= range;
            while (currentAngle < min) currentAngle += range;
        }
        else
        {
            if (currentAngle >= max)
            {
                currentAngle = max;
                direction = -1;
            }
            else if (currentAngle <= min)
            {
                currentAngle = min;
                direction = 1;
            }
        }

        Vector3 euler = transform.localEulerAngles;
        euler.z = currentAngle;
        transform.localEulerAngles = euler;
    }

    float NormalizeAngle(float angle)
    {
        while (angle > 360f) angle -= 360f;
        while (angle < 0f) angle += 360f;
        return angle;
    }
}
