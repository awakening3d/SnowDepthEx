using System;
using FlaxEngine;

public class Ball : Script
{

    public float MinHitVelocity = 100f;	

    private RigidBody _rb;
	

    public override void OnEnable()
    {
        _rb = Actor.As<RigidBody>();
        if(_rb != null)
        {
            _rb.CollisionEnter += OnRbCollisionEnter;
        }
    }

    public override void OnDisable()
    {
        if(_rb != null)
        {
            _rb.CollisionEnter -= OnRbCollisionEnter;
        }
    }

    void OnRbCollisionEnter(Collision collision)
    {
		//Debug.LogWarning("On Collision Enter");
        float hitSpeed = collision.RelativeVelocity.LengthSquared;
        if (hitSpeed < MinHitVelocity*MinHitVelocity) return;

        if ( 0 == collision.Contacts.Length ) return;
        var contact = collision.Contacts[0];
		SpawnFootprint(contact.Point);
    }
	
	
	void SpawnFootprint(Vector3 worldPos)
	{
		Actor snowManagerActor = Actor.Scene.FindActor("SnowManager");
		if (snowManagerActor == null)
		{
			Debug.LogWarning("SpawnFootprint: can not find SnowManager Actor");
			return;
		}

		SnowDepthRTManager snowMgr = snowManagerActor.GetScript<SnowDepthRTManager>();
		if (snowMgr == null)
		{
			Debug.LogWarning("SpawnFootprint: SnowManager has no [SnowDepthRTManager] script");
			return;
		}

		Vector2 footWorldXZ = new Vector2(worldPos.X, worldPos.Z);

		float footRadius = 90f;
		float footPressure = 0.9f;

		snowMgr.DrawFootprint(footWorldXZ, footRadius, footPressure);
	}	

}