using UnityEngine;

public class KeepUp : MonoBehaviour
{
    void LateUpdate()
    {
        transform.eulerAngles = Vector3.zero;
    }
}
