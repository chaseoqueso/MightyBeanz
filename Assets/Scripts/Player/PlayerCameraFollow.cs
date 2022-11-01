using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using static UnityEngine.InputSystem.InputAction;

public class PlayerCameraFollow : MonoBehaviour
{
    private PlayerMovement player;

    void Awake()
    {
        player = GetComponentInParent<PlayerMovement>();
        transform.parent = null;
    }

    void Update()
    {
        transform.position = Vector3.Lerp(transform.position, player.GetStablePosition(), 0.5f);
    }
}
