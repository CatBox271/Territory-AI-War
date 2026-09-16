using System.Collections;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEngine;

public class MarbleManager : MonoBehaviour
{
    public enum UpgradeChoice { Marble = 1, Turret = 2, Shield = 3 }

    public static MarbleManager Instance;

    [Header("Prefabs & Refs")]
    public GameObject MarbleOb;
    public Transform Shooter;
    public Transform spawnArea;

    [Header("Settings")]
    public int initialMarbleCount = 3;
    public float initialSpawnDelay = 0.3f;
    public uint initialValueExponent = 10;
    public uint FirstValueExponent = 20;
    public uint startValueExponent = 10;
    public HugeInt maxValue;
    public float gravity = 0.1f;

    private int teamCount;
    private List<GameObject>[] teamMarbleObs;

    private Shooter shooterComp;

    [Header("空槽升级机制")]
    [Tooltip("每个空着的已解锁道具槽，每秒积累的升级值")]
    public float upgradePerEmptySlotPerSecond = 1f;
    [Tooltip("升级值达到该数值后，给对应 AI 阵营额外生成一个弹珠")]
    public float upgradeCost = 100f;
    [Tooltip("炮塔移动一次消耗的升级能量（固定单次扣除，与移动距离无关）")]
    public float moveEnergyCost = 50f;

    // 参与空槽升级的 AI 阵营，以及各自的升级进度。
    private readonly HashSet<int> aiStages = new();
    private readonly Dictionary<int, float> upgradeProgress = new();
    private readonly Dictionary<int, float> upgradeCosts = new();

    // 已升级次数：阵营 -> 次数（用于在升级选择时告诉 AI“你已经选过几次”，避免总选同一个）
    private readonly Dictionary<int, int> marbleUpgradeCount = new();
    private readonly Dictionary<int, int> turretUpgradeCount = new();
    private readonly Dictionary<int, int> shieldUpgradeCount = new();

    [Tooltip("每生成一个弹珠后，该阵营下一次升级所需值乘以这个倍率")]
    public float upgradeCostGrowth = 1.5f;

    private void Awake()
    {
        Instance = this;
    }

    /// <summary>登记参与空槽升级机制的 AI 阵营。</summary>
    public void RegisterAIStage(int aiStage)
    {
        if (aiStage <= 0) return;
        aiStages.Add(aiStage);
        if (!upgradeProgress.ContainsKey(aiStage))
        {
            upgradeProgress[aiStage] = 0f;
            upgradeCosts[aiStage] = upgradeCost;
        }
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

    bool first = true;
    IEnumerator SpawnInitial()
    {
        for (int i = 0; i < initialMarbleCount; i++)
        {
            for (int stage = 1; stage <= teamCount; stage++)
                SpawnAndLaunch(stage);
            yield return new WaitForSeconds(initialSpawnDelay);
        }
        first = false;
    }

    void Update()
    {
        UpdateEmptySlotUpgrade();
    }

    /// <summary>空槽升级进度：AI 模式按空槽数累计；非 AI 模式每个弹珠固定每秒 +1。</summary>
    private void UpdateEmptySlotUpgrade()
    {
        if (MapConfig.Instance == null || MapConfig.Instance.teamProps == null) return;
        if (upgradePerEmptySlotPerSecond <= 0f || upgradeCost <= 0f) return;

        MapConfig config = MapConfig.Instance;
        if (config.useAIDecision)
        {
            foreach (int aiStage in aiStages)
            {
                if (aiStage < 0 || aiStage >= config.teamProps.Length) continue;
                List<PropEntry> props = config.teamProps[aiStage];
                if (props == null) continue;

                int emptySlots = Mathf.Max(0, config.propLimit - props.Count);
                if (emptySlots <= 0) continue;

                AdvanceUpgrade(aiStage, emptySlots * upgradePerEmptySlotPerSecond * Time.deltaTime);
            }
        }
        else
        {
            // AI 模式关闭：当前每个弹珠的升级进度固定每秒 +1
            for (int stage = 1; stage <= teamCount; stage++)
                AdvanceUpgrade(stage, Time.deltaTime);
        }
    }

    private void AdvanceUpgrade(int stage, float gain)
    {
        if (gain <= 0f) return;

        if (!upgradeProgress.ContainsKey(stage))
        {
            upgradeProgress[stage] = 0f;
            upgradeCosts[stage] = upgradeCost;
        }

        float progress = upgradeProgress[stage];
        progress += gain;
        float cost = upgradeCosts.TryGetValue(stage, out float c) ? c : upgradeCost;

        while (progress >= cost)
        {
            progress -= cost;
            cost *= upgradeCostGrowth;

            UIMarbleUpgrade.Instance.ShowUpgrade(stage);
            if (MapConfig.Instance.useAIDecision && AIAgent.Instance != null)
                StartCoroutine(AIUpgradeSequence(stage));
            else
                StartCoroutine(UpgradeSpawnSequence(stage));
        }

        upgradeCosts[stage] = cost;
        upgradeProgress[stage] = progress;
    }

    /// <summary>
    /// useAI 模式下：询问 AI 选择升级，选择后执行对应升级。
    /// 舞台演出可用时，整件事交给 UpgradeChoiceScene 演（三张选项卡 + 先思考再揭晓），
    /// 演出播完再让升级真正生效 —— 这样炮塔升级的连线与飘字仍然留在战场上播。
    /// </summary>
    IEnumerator AIUpgradeSequence(int stage)
    {
        if (AIAgent.Instance == null)
        {
            ApplyUpgradeChoice(stage, UpgradeChoice.Marble);
            yield break;
        }

        if (StoryTeller.CanPlay)
        {
            var scene = new UpgradeChoiceScene(stage);
            StoryTeller.Instance.Play(scene);
            while (!scene.Finished)
                yield return null;
            ApplyUpgradeChoice(stage, (UpgradeChoice)scene.Choice);
            yield break;
        }

        // 演出关掉时的旧路径：直接问 AI，不进舞台
        Task<int> choiceTask = AIAgent.Instance.RequestUpgradeChoiceAsync(stage);
        while (!choiceTask.IsCompleted)
            yield return null;

        int choice = 1;
        if (choiceTask.IsFaulted || choiceTask.IsCanceled)
        {
            Debug.LogWarning($"[Upgrade] stage {stage} 升级选择请求失败，默认 +1 弹珠。{choiceTask.Exception}");
        }
        else
        {
            choice = choiceTask.Result;
        }
        ApplyUpgradeChoice(stage, (UpgradeChoice)choice);
    }

    private static string UpgradeChoiceText(int choice)
    {
        switch (choice)
        {
            case 2: return "炮塔升级";
            case 3: return "护盾升级";
            default: return "弹珠+1";
        }
    }

    public void ApplyUpgradeChoice(int stage, UpgradeChoice choice)
    {
        switch (choice)
        {
            case UpgradeChoice.Turret:
                BumpUpgradeCount(turretUpgradeCount, stage);
                StartCoroutine(TurretUpgradeSequence(stage, UpgradeChoice.Turret));
                break;
            case UpgradeChoice.Shield:
                BumpUpgradeCount(shieldUpgradeCount, stage);
                StartCoroutine(TurretUpgradeSequence(stage, UpgradeChoice.Shield));
                break;
            default:
                BumpUpgradeCount(marbleUpgradeCount, stage);
                StartCoroutine(UpgradeSpawnSequence(stage));
                break;
        }
    }

    private static void BumpUpgradeCount(Dictionary<int, int> table, int stage)
    {
        table[stage] = (table.TryGetValue(stage, out int c) ? c : 0) + 1;
    }

    /// <summary>查询某阵营已经升级过几次（额外弹珠 / 炮塔强化 / 护盾强化）。</summary>
    public bool TryGetUpgradeCounts(int stage, out int marble, out int turret, out int shield)
    {
        marble = marbleUpgradeCount.TryGetValue(stage, out int m) ? m : 0;
        turret = turretUpgradeCount.TryGetValue(stage, out int t) ? t : 0;
        shield = shieldUpgradeCount.TryGetValue(stage, out int s) ? s : 0;
        return aiStages.Contains(stage) || marble + turret + shield > 0;
    }

    /// <summary>炮塔/护盾升级：从升级槽连线到炮塔，线到后再应用升级并弹上升文本。</summary>
    IEnumerator TurretUpgradeSequence(int stage, UpgradeChoice choice)
    {
        if (!Towel.AllTowel.TryGetValue(stage, out Towel towel) || towel == null)
            yield break;

        Vector3 target = towel.transform.position;
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui != null)
            yield return PlayLineToTarget(stage, target);

        if (choice == UpgradeChoice.Turret) towel.ApplyTurretUpgrade();
        else towel.ApplyShieldUpgrade();

        string label = choice == UpgradeChoice.Turret ? "炮塔升级" : "护盾升级";
        towel.ShowTip(label);
    }

    IEnumerator PlayLineToTarget(int stage, Vector3 target)
    {
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui == null) yield break;

        bool arrived = false;
        ui.PlayLineTo(stage, target, () => arrived = true);
        float waited = 0f;
        while (!arrived)
        {
            waited += Time.deltaTime;
            if (waited > 3f) break;
            yield return null;
        }
    }

    IEnumerator UpgradeSpawnSequence(int stage)
    {
        Vector3 target = GetSpawnPosition();
        UIMarbleUpgrade ui = UIMarbleUpgrade.Instance;
        if (ui != null)
        {
            bool arrived = false;
            ui.PlayLineTo(stage, target, () => arrived = true);
            float waited = 0f;
            while (!arrived)
            {
                waited += Time.deltaTime;
                if (waited > 3f) break;
                yield return null;
            }
        }
        SpawnAndLaunchAt(stage, target);
    }


    /// <summary>查询指定阵营当前的弹珠升级进度与下一次升级所需值。</summary>
    public bool TryGetUpgradeInfo(int stage, out float progress, out float cost)
    {
        progress = upgradeProgress.TryGetValue(stage, out float p) ? p : 0f;
        cost = upgradeCosts.TryGetValue(stage, out float c) ? c : upgradeCost;
        return aiStages.Contains(stage) || upgradeProgress.ContainsKey(stage);
    }

    /// <summary>当前可用的升级能量（即空槽升级进度）。</summary>
    public float GetUpgradeEnergy(int stage)
    {
        return upgradeProgress.TryGetValue(stage, out float p) ? p : 0f;
    }

    /// <summary>尝试扣除升级能量：足够则扣掉并返回 true；不够则返回 false 且不扣。</summary>
    public bool TrySpendUpgradeEnergy(int stage, float amount)
    {
        if (amount <= 0f) return true;
        float progress = GetUpgradeEnergy(stage);
        if (progress < amount) return false;
        upgradeProgress[stage] = progress - amount;
        return true;
    }
    /// <summary>供空槽升级等系统外部调用，给指定阵营额外生成并发射一个弹珠。</summary>
    public void SpawnMarbleForStage(int stage)
    {
        SpawnAndLaunch(stage);
    }

    void SpawnAndLaunch(int stage)
    {
        SpawnAndLaunchAt(stage, GetSpawnPosition());
    }

    void SpawnAndLaunchAt(int stage, Vector3 pos)
    {
        if (MarbleOb == null) return;
        if (!Towel.AllTowel.ContainsKey(stage)) return;

        GameObject ob = Instantiate(MarbleOb, pos, Quaternion.identity);

        Marble m = ob.GetComponent<Marble>();
        if (m != null)
        {
            m.stage = stage;
            m.SetInitialValue(first? FirstValueExponent: startValueExponent);
            //注册到InformGeter供AI数据收集
            InformGetter.AddMarble(stage, m);
        }

        if (shooterComp != null)
        {
            Rigidbody2D rb = ob.GetComponent<Rigidbody2D>();
            if (rb != null) shooterComp.Launch(rb);
        }

        var col = ob.GetComponent<Collider2D>();
        foreach (var teamob in teamMarbleObs[stage])
        {
            //同队无碰撞
            Physics2D.IgnoreCollision(col, teamob.GetComponent<Collider2D>());
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

    public Shooter GetShooter()
    {
        return Shooter != null ? Shooter.GetComponent<Shooter>() : null;
    }
}
