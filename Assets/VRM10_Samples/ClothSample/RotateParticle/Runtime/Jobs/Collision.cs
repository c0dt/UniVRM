using System;
using System.Collections.Generic;
using SphereTriangle;
using UniGLTF.SpringBoneJobs.Blittables;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;


namespace RotateParticle.Jobs
{
    public struct InputColliderJob : IJobParallelForTransform
    {
        [WriteOnly] public NativeArray<Matrix4x4> CurrentCollider;

        public void Execute(int colliderIndex, TransformAccess transform)
        {
            CurrentCollider[colliderIndex] = transform.localToWorldMatrix;
        }
    }

    public struct StrandCollisionJob : IJobParallelFor
    {
        // collider
        [ReadOnly] public NativeArray<BlittableCollider> Colliders;
        [ReadOnly] public NativeArray<Matrix4x4> CurrentColliders;

        // particle
        [ReadOnly] public NativeArray<TransformInfo> Info;
        [ReadOnly] public NativeArray<Vector3> NextPositions;
        [ReadOnly] public NativeArray<bool> ClothUsedParticles;
        [WriteOnly] public NativeArray<Vector3> StrandCollision;

        public void Execute(int particleIndex)
        {
            if (!ClothUsedParticles[particleIndex])
            {
                var info = Info[particleIndex];
                var pos = NextPositions[particleIndex];
                for (int colliderIndex = 0; colliderIndex < Colliders.Length; ++colliderIndex)
                {
                    var collider = Colliders[colliderIndex];
                    var col_pos = CurrentColliders[colliderIndex].MultiplyPoint(collider.offset);
                    switch (collider.colliderType)
                    {
                        case BlittableColliderType.Sphere:
                            {
                                var d = Vector3.Distance(pos, col_pos);
                                var min_d = info.Settings.radius + collider.radius;
                                if (d < min_d)
                                {
                                    Vector3 normal = (pos - col_pos).normalized;
                                    pos += normal * (min_d - d);
                                }
                                break;
                            }

                        default:
                            break;
                    }
                }
                StrandCollision[particleIndex] = pos;
            }
        }
    }

    public struct ClothCollisionJob : IJobParallelFor
    {
        // collider
        [ReadOnly] public NativeArray<BlittableCollider> Colliders;
        [ReadOnly] public NativeArray<Matrix4x4> CurrentColliders;

        // particle
        [ReadOnly] public NativeArray<TransformInfo> Info;
        [ReadOnly] public NativeArray<Vector3> NextPositions;
        [NativeDisableParallelForRestriction] public NativeArray<int> CollisionCount;
        [NativeDisableParallelForRestriction] public NativeArray<Vector3> CollisionDelta;

        // cloth
        [ReadOnly] public NativeArray<(SpringConstraint, ClothRect)> ClothRects;

        private void CollisionMove(int particleIndex, Vector3 delta)
        {
            CollisionCount[particleIndex] += 1;
            CollisionDelta[particleIndex] += delta;
        }

        public void Execute(int rectIndex)
        {
            var (spring, rect) = ClothRects[rectIndex];

            // using (new ProfileSample("Rect: Prepare"))
            // _s0.BeginFrame();
            // _s1.BeginFrame();

            var a = NextPositions[rect._a];
            var b = NextPositions[rect._b];
            var c = NextPositions[rect._c];
            var d = NextPositions[rect._d];
            var aabb = GetBoundsFrom4(a, b, c, d);

            // d x-x c
            //   |/
            // a x
            var _triangle1 = new Triangle(c, d, a);
            //     x c
            //    /|
            // a x-x b
            var _triangle0 = new Triangle(a, b, c);

            for (int colliderIndex = 0; colliderIndex < Colliders.Length; ++colliderIndex)
            {
                var collider = Colliders[colliderIndex];
                var collider_matrix = CurrentColliders[colliderIndex];
                if (!aabb.Intersects(GetBounds(collider, collider_matrix)))
                {
                    continue;
                }

                // 面の片側だけにヒットさせる
                // 行き過ぎて戻るときに素通りする
                // var p = _triangle0.Plane.ClosestPointOnPlane(col_pos);
                // var dot = Vector3.Dot(_triangle0.Plane.normal, col_pos - p);
                // if (_initialColliderNormalSide[collider] * dot < 0)
                // {
                //     // 片側
                //     continue;
                // }

                if (TryCollide(collider, collider_matrix, _triangle0, out var l0))
                {
                    CollisionMove(rect._a, l0.GetDelta(collider.radius));
                    CollisionMove(rect._b, l0.GetDelta(collider.radius));
                    CollisionMove(rect._c, l0.GetDelta(collider.radius));
                }
                if (TryCollide(collider, collider_matrix, _triangle1, out var l1))
                {
                    CollisionMove(rect._c, l1.GetDelta(collider.radius));
                    CollisionMove(rect._d, l1.GetDelta(collider.radius));
                    CollisionMove(rect._a, l1.GetDelta(collider.radius));
                }
            }
        }

        static bool TryCollide(BlittableCollider collider, in Matrix4x4 colliderMatrix, in Triangle t, out LineSegment l)
        {
            var col_pos = colliderMatrix.MultiplyPoint(collider.offset);
            if (collider.colliderType == BlittableColliderType.Capsule)
            {
                // capsule
                var tail_pos = colliderMatrix.MultiplyPoint(collider.tailOrNormal);
                var result = TriangleCapsuleCollisionSolver.Collide(t, new LineSegment(col_pos, tail_pos), collider.radius);
                var type = result.TryGetClosest(out l);
                return type.HasValue;
            }
            else
            {
                // sphere
                return TryCollideSphere(t, col_pos, collider.radius, out l);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="triangle"></param>
        /// <param name="collider"></param>
        /// <param name="radius"></param>
        /// <returns>collider => 衝突点 への線分を返す</returns>
        static bool TryCollideSphere(in Triangle triangle, in Vector3 collider, float radius, out LineSegment l)
        {
            var p = triangle.Plane.ClosestPointOnPlane(collider);
            var distance = Vector3.Distance(p, collider);
            if (distance > radius)
            {
                l = default;
                return false;
            }

            if (triangle.IsSameSide(p))
            {
                l = new LineSegment(collider, p);
                return true;
            }

            var (closestPoint, d) = triangle.GetClosest(collider);
            if (d > radius)
            {
                l = default;
                return false;
            }
            l = new LineSegment(collider, closestPoint);
            return true;
        }

        public static Bounds GetBoundsFrom4(in Vector3 a, in Vector3 b, in Vector3 c, in Vector3 d)
        {
            var aabb = new Bounds(a, Vector3.zero);
            aabb.Encapsulate(b);
            aabb.Encapsulate(c);
            aabb.Encapsulate(d);
            return aabb;
        }

        public static Bounds GetBounds(BlittableCollider collider, Matrix4x4 m)
        {
            switch (collider.colliderType)
            {
                case BlittableColliderType.Capsule:
                    {
                        var h = m.MultiplyPoint(collider.offset);
                        var t = m.MultiplyPoint(collider.tailOrNormal);
                        var d = h - t;
                        var aabb = new Bounds((h + t) * 0.5f, new Vector3(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z)));
                        aabb.Expand(collider.radius * 2);
                        return aabb;
                    }

                case BlittableColliderType.Sphere:
                    return new Bounds(m.MultiplyPoint(collider.offset), new Vector3(collider.radius, collider.radius, collider.radius));

                default:
                    throw new NotImplementedException();
            }
        }

    }

    public struct CollisionApplyJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<bool> ClothUsedParticles;
        [ReadOnly] public NativeArray<Vector3> StrandCollision;
        [ReadOnly] public NativeArray<int> ClothCollisionCount;
        [ReadOnly] public NativeArray<Vector3> ClothCollisionDelta;
        [NativeDisableParallelForRestriction] public NativeArray<Vector3> NextPosition;

        public void Execute(int particleIndex)
        {
            if (ClothUsedParticles[particleIndex])
            {
                if (ClothCollisionCount[particleIndex] > 0)
                {
                    NextPosition[particleIndex] += (ClothCollisionDelta[particleIndex] / ClothCollisionCount[particleIndex]);
                }
            }
            else
            {
                NextPosition[particleIndex] = StrandCollision[particleIndex];
            }
        }
    }
}