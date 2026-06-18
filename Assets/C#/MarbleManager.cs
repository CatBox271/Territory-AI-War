using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MarbleManager : MonoBehaviour
{
    public static MarbleManager Instance;

    [Header("Prefabs & Refs")]
    public GameObject MarbleOb;
    public Transform Shooter;
    public Transform spawnArea;

    [Header("Settings")]
    public int initialMarbleCount = 3;
    public float initialSpawnDelay = 0.3f;
    public float spawnInterval = 5f;
    public uint initialValueExponent = 10;
    public uint startValueExponent = 10;
    public HugeInt maxValue;
    public float gravity = 0.1f;

    private int teamCount;
    private List<GameObject>[] teamMarbleObs;
    private float timer;
    private Shooter shooterComp;

    private void Awake()
    {
        Instance = this;
    }
    void Start()
    {
        if (Shooter != null)
            shooterComp = Shooter.GetComponent<Shooter>();

        teamCount = MapConfig.Instance.teamColors.Count - 1;
        teamMarbleObs = new List<GameObject>[teamCount + 1];

        for (int stage = 1; stage <= teamCount; stage++)
            teamMarbleObs[stage] = new List<GameObject>();

        StartCoroutine(SpawnInitial());
    }

    IEnumerator SpawnInitial()
    {
        for (int i = 0; i < initialMarbleCount; i++)
        {
            for (int stage = 1; stage <= teamCount; stage++)
                SpawnAndLaunch(stage);
            yield return new WaitForSeconds(initialSpawnDelay);
        }
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= spawnInterval * 60f)
        {
            timer -= spawnInterval * 60f;
            for (int stage = 1; stage <= teamCount; stage++)
                SpawnAndLaunch(stage);
        }
    }

    void SpawnAndLaunch(int stage)
    {
        if (MarbleOb == null) return;

        Vector3 pos = GetSpawnPosition();
        GameObject ob = Instantiate(MarbleOb, pos, Quaternion.identity);

        Marble m = ob.GetComponent<Marble>();
        if (m != null)
        {
            m.stage = stage;
            m.SetInitialValue(startValueExponent);
        }

        if (shooterComp != null)
        {
            Rigidbody2D rb = ob.GetComponent<Rigidbody2D>();
            if (rb != null) shooterComp.Launch(rb);
        }

        teamMarbleObs[stage].Add(ob);
    }

    Vector3 GetSpawnPosition()
    {
        if (spawnArea != null)
        {
            Vector3 center = spawnArea.position;
            Vector3 half = spawnArea.lossyScale * 0.5f;
            float x = Random.Range(center.x - half.x, center.x + half.x);
            float y = Random.Range(center.y - half.y, center.y + half.y);
            return new Vector3(x, y, center.z);
        }
        return Shooter != null ? Shooter.position : transform.position;
    }

    public void OnTeamDeath(int stage)
    {
        if (stage <= 0 || stage > teamCount) return;
        var list = teamMarbleObs[stage];
        if (list == null) return;
        foreach (var ob in list)
            if (ob != null) Destroy(ob);
        list.Clear();
    }
}
