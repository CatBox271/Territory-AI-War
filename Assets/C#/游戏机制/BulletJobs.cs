using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using static BulletManager;

[BurstCompile]
public struct MoveBulletsJob : IJobParallelFor
{
    public NativeArray<BulletData> bullets;
    public float dt;
    public float mapHalfSize;

    public void Execute(int index)
    {
        var b = bullets[index];
        if (b.alive == 0) return;

        b.oldPosition = b.position;
        float2 newPos = b.position + b.velocity * dt;

        if (math.abs(newPos.x) > mapHalfSize)
        { newPos.x = math.sign(newPos.x) * mapHalfSize; b.velocity.x = -b.velocity.x; }
        if (math.abs(newPos.y) > mapHalfSize)
        { newPos.y = math.sign(newPos.y) * mapHalfSize; b.velocity.y = -b.velocity.y; }
        b.position = newPos;

        bullets[index] = b;
    }
}

[BurstCompile]
public struct BulletBallCollisionJob : IJobParallelFor
{
    public NativeArray<BulletData> bullets;
    [ReadOnly] public NativeArray<BallCollider> balls;
    public int ballCount;
    public NativeList<BulletHit>.ParallelWriter hitWriter;

    public void Execute(int index)
    {
        var b = bullets[index];
        if (b.alive == 0 || b.value <= 0) return;

        for (int i = 0; i < ballCount; i++)
        {
            var ball = balls[i];
            if (math.distancesq(b.position, ball.position) >= math.pow(ball.radius + 0.05f, 2)) continue;

            bool sameTeam = b.stage == ball.stage;
            hitWriter.AddNoResize(new BulletHit
            {
                targetType = 0, targetIndex = i, bulletIndex = index,
                sameTeam = sameTeam, value = b.value,
                attackPower = b.attackPower, bulletVelocity = b.velocity
            });

            if (sameTeam || b.value <= ball.value)
            { var d = b; d.alive = 0; d.value = 0; bullets[index] = d; }
            else
            { b.value -= ball.value; bullets[index] = b; }
            return;
        }
    }
}

[BurstCompile]
public struct BulletShieldCollisionJob : IJobParallelFor
{
    public NativeArray<BulletData> bullets;
    [ReadOnly] public NativeArray<ShieldCollider> shields;
    public int shieldCount;
    public NativeList<BulletHit>.ParallelWriter hitWriter;

    public void Execute(int index)
    {
        var b = bullets[index];
        if (b.alive == 0 || b.value <= 0) return;

        for (int i = 0; i < shieldCount; i++)
        {
            var shield = shields[i];
            if (math.distancesq(b.position, shield.position) >= shield.radius * shield.radius) continue;
            if (b.stage == shield.stage) return; // 同队穿过

            hitWriter.AddNoResize(new BulletHit
            {
                targetType = 1, targetIndex = i, bulletIndex = index,
                sameTeam = false, value = b.value,
                attackPower = b.attackPower, bulletVelocity = b.velocity
            });

            if (b.value <= shield.value)
            { var d = b; d.alive = 0; d.value = 0; bullets[index] = d; }
            else
            { b.value -= shield.value; bullets[index] = b; }
            return;
        }
    }
}

[BurstCompile]
public struct BulletTowelCollisionJob : IJobParallelFor
{
    public NativeArray<BulletData> bullets;
    [ReadOnly] public NativeArray<ShieldCollider> towelBodies;
    public int towelCount;
    public NativeList<BulletHit>.ParallelWriter hitWriter;

    public void Execute(int index)
    {
        var b = bullets[index];
        if (b.alive == 0 || b.value <= 0) return;

        for (int i = 0; i < towelCount; i++)
        {
            var body = towelBodies[i];
            if (math.distancesq(b.position, body.position) >= body.radius * body.radius) continue;
            if (b.stage == body.stage) return;

            hitWriter.AddNoResize(new BulletHit
            {
                targetType = 2, targetIndex = i, bulletIndex = index,
                sameTeam = false, value = b.value,
                attackPower = b.attackPower, bulletVelocity = b.velocity
            });

            var d = b; d.alive = 0; d.value = 0; bullets[index] = d;
            return;
        }
    }
}
