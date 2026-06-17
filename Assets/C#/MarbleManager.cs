using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MarbleManager : MonoBehaviour
{
    public static MarbleManager Instance;

    [Header("Prefabs & Refs")]
    public GameObject MarbleOb;
    public Transform Shooter;

    [Header("Settings")]
    public int initialMarbleCount = 3;
    public float initialSpawnDelay = 0.3f;
    public float spawnInterval = 5f;
    public uint initialValueExponent = 10;
    public HugeInt maxValue;

    private int teamCount;
    private List<GameObject>[] teamMarbleObs;
    private float timer;
    private Shooter shooterComp;

    void Awake()
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

        Vector3 pos = Shooter != null ? Shooter.position : transform.position;
        GameObject ob = Instantiate(MarbleOb, pos, Quaternion.identity);

        Marble m = ob.GetComponent<Marble>();
        if (m != null)
        {
            m.stage = stage;
            m.SetInitialValue(initialValueExponent);
        }

        if (shooterComp != null)
        {
            Rigidbody2D rb = ob.GetComponent<Rigidbody2D>();
            if (rb != null) shooterComp.Launch(rb);
        }

        teamMarbleObs[stage].Add(ob);
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
