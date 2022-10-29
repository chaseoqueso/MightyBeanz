using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TransformAndTime
{
    public float time;
    public Vector3 position;
    public Quaternion rotation;

    public TransformAndTime(float time, Vector3? position = null, Quaternion? rotation = null)
    {
        this.time = time;
        this.position = (position.HasValue ? position.Value : Vector3.zero);
        this.rotation = (rotation.HasValue ? rotation.Value : Quaternion.identity);
    }

    public TransformAndTime(float time, Transform transform)
    {
        this.time = time;
        this.position = transform.position;
        this.rotation = transform.rotation;
    }
}

public class CollisionHit
{
    public readonly Collider collider;
    public readonly Vector3 point;
    public readonly TransformAndTime transform;

    public CollisionHit(Collider collider, TransformAndTime transform, Vector3? point = null)
    {
        this.collider = collider;
        this.transform = transform;
        this.point = point.HasValue ? point.Value : Vector3.zero;
    }
}

public static class Utilities
{
}
