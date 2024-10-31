using Sandbox.Citizen;

/// <summary>
/// A common base we can use for weapons so we don't have to implement the logic over and over
/// again. Feel free to not use this and to implement it however you want to.
/// </summary>
[Icon( "sports_martial_arts" )]
public partial class BaseWeapon : Component
{
	[Property] public GameObject ViewModelPrefab { get; set; }
	[Property] public string ParentBone { get; set; } = "hold_r";
	[Property] public Transform BoneOffset { get; set; } = new Transform( 0 );
	[Property] public CitizenAnimationHelper.HoldTypes HoldType { get; set; } = CitizenAnimationHelper.HoldTypes.HoldItem;
	[Property] public CitizenAnimationHelper.Hand Handedness { get; set; } = CitizenAnimationHelper.Hand.Right;
	[Property] public float PrimaryRate { get; set; } = 5.0f;
	[Property] public float SecondaryRate { get; set; } = 15.0f;
	[Property] public float ReloadTime { get; set; } = 3.0f;

	[Sync] public bool IsReloading { get; set; }
	[Sync] public RealTimeSince TimeSinceReload { get; set; }
	[Sync] public RealTimeSince TimeSinceDeployed { get; set; }
	[Sync] public RealTimeSince TimeSincePrimaryAttack { get; set; }
	[Sync] public RealTimeSince TimeSinceSecondaryAttack { get; set; }

	public ViewModel ViewModel => Scene?.Camera?.GetComponentsInChildren<ViewModel>( true ).FirstOrDefault( x => x.GameObject.Name == ViewModelPrefab.Name );
	public Player Owner => GameObject?.Root?.GetComponent<Player>();

	protected override void OnAwake()
	{
		if ( IsProxy ) return;

		ViewModelPrefab?.Clone( new CloneConfig()
		{
			StartEnabled = false,
			Parent = Scene.Camera.GameObject,
			Transform = Scene.Camera.WorldTransform
		} );
	}

	protected override void OnEnabled()
	{
		TimeSinceDeployed = 0;

		if ( IsProxy ) return;

		ViewModel.GameObject.Enabled = true;
	}

	protected override void OnDisabled()
	{
		if ( IsProxy ) return;

		ViewModel.GameObject.Enabled = false;
	}

	protected override void OnDestroy()
	{
		if ( IsProxy ) return;

		if ( ViewModel.IsValid() ) ViewModel.DestroyGameObject();
	}

	protected override void OnUpdate()
	{
		GameObject.NetworkInterpolation = false;

		Owner?.Controller?.Renderer?.Set( "holdtype", (int)HoldType );
		Owner?.Controller?.Renderer?.Set( "holdtype_handedness", (int)Handedness );

		var obj = Owner?.Controller?.Renderer?.GetBoneObject( ParentBone );
		if ( obj is not null )
		{
			GameObject.Parent = obj;
			GameObject.LocalTransform = BoneOffset.WithScale( 1 );
		}

		if ( IsProxy )
			return;

		ViewModel.GameObject.Tags.Set( "viewer", Owner.Controller.ThirdPerson );

		OnControl();
	}

	public virtual void OnControl()
	{
		if ( TimeSinceDeployed < 0.6f )
			return;

		if ( !IsReloading )
		{
			if ( IsProxy )
				return;

			if ( CanReload() )
			{
				Reload();
			}

			//
			// Reload could have changed our owner
			//
			if ( Owner == null )
				return;

			if ( CanPrimaryAttack() )
			{
				TimeSincePrimaryAttack = 0;
				AttackPrimary();
			}

			//
			// AttackPrimary could have changed our owner
			//
			if ( Owner == null )
				return;

			if ( CanSecondaryAttack() )
			{
				TimeSinceSecondaryAttack = 0;
				AttackSecondary();
			}
		}

		if ( IsReloading && TimeSinceReload > ReloadTime )
		{
			OnReloadFinish();
		}
	}

	public virtual void OnReloadFinish()
	{
		IsReloading = false;
	}

	public virtual void StartReloadEffects()
	{
		ViewModel?.Renderer?.Set( "b_reload", true );
	}

	// TODO: Probably should unify these particle methods + make it work for world models

	protected virtual void ShootEffects()
	{
		if ( ViewModel.Tags.Has( "viewer" ) )
			return;

		var particleSystem = ParticleSystem.Load( "particles/pistol_muzzleflash.vpcf" );

		var go = new GameObject
		{
			Name = particleSystem.Name,
			Parent = ViewModel.GameObject,
			WorldTransform = ViewModel?.Renderer?.GetAttachment( "muzzle" ) ?? default
		};

		var legacyParticleSystem = go.AddComponent<LegacyParticleSystem>();
		legacyParticleSystem.Particles = particleSystem;
		legacyParticleSystem.ControlPoints = new()
		{
			new ParticleControlPoint { GameObjectValue = go, Value = ParticleControlPoint.ControlPointValueInput.GameObject }
		};

		go.DestroyAsync();

		ViewModel?.Renderer?.Set( "fire", true );
	}

	public virtual bool CanReload()
	{
		if ( Owner == null || !Input.Down( "reload" ) ) return false;

		return true;
	}

	public virtual void Reload()
	{
		if ( IsReloading )
			return;

		TimeSinceReload = 0;
		IsReloading = true;

		BroadcastReload();
		StartReloadEffects();
	}

	[Broadcast]
	private void BroadcastReload()
	{
		Owner?.Controller?.Renderer?.Set( "b_reload", true );
	}

	public virtual bool CanPrimaryAttack()
	{
		if ( Owner == null || !Input.Down( "attack1" ) ) return false;

		var rate = PrimaryRate;
		if ( rate <= 0 ) return true;

		return TimeSincePrimaryAttack > (1 / rate);
	}

	public virtual void AttackPrimary()
	{

	}

	public virtual bool CanSecondaryAttack()
	{
		if ( Owner == null || !Input.Down( "attack2" ) ) return false;

		var rate = SecondaryRate;
		if ( rate <= 0 ) return true;

		return TimeSinceSecondaryAttack > (1 / rate);
	}

	public virtual void AttackSecondary()
	{

	}

	/// <summary>
	/// Does a trace from start to end, does bullet impact effects. Coded as an IEnumerable so you can return multiple
	/// hits, like if you're going through layers or ricocheting or something.
	/// </summary>
	public virtual IEnumerable<SceneTraceResult> TraceBullet( Vector3 start, Vector3 end, float radius = 2.0f )
	{
		// bool underWater = Trace.TestPoint( start, "water" );

		var trace = Scene.Trace.Ray( start, end )
				.UseHitboxes()
				.WithAnyTags( "solid", "player", "npc", "glass" )
				.IgnoreGameObjectHierarchy( GameObject.Root )
				.Size( radius );

		//
		// If we're not underwater then we can hit water
		//
		/*
		if ( !underWater )
			trace = trace.WithAnyTags( "water" );
		*/

		var tr = trace.Run();

		if ( tr.Hit )
			yield return tr;

		//
		// Another trace, bullet going through thin material, penetrating water surface?
		//
	}

	public IEnumerable<SceneTraceResult> TraceMelee( Vector3 start, Vector3 end, float radius = 2.0f )
	{
		var trace = Scene.Trace.Ray( start, end )
				.UseHitboxes()
				.WithAnyTags( "solid", "player", "npc", "glass" )
				.IgnoreGameObjectHierarchy( GameObject.Root );

		var tr = trace.Run();

		if ( tr.Hit )
		{
			yield return tr;
		}
		else
		{
			trace = trace.Size( radius );

			tr = trace.Run();

			if ( tr.Hit )
			{
				yield return tr;
			}
		}
	}

	/// <summary>
	/// Shoot a single bullet
	/// </summary>
	public virtual void ShootBullet( Vector3 pos, Vector3 dir, float spread, float force, float damage, float bulletSize )
	{
		var forward = dir;
		forward += (Vector3.Random + Vector3.Random + Vector3.Random + Vector3.Random) * spread * 0.25f;
		forward = forward.Normal;

		//
		// ShootBullet is coded in a way where we can have bullets pass through shit
		// or bounce off shit, in which case it'll return multiple results
		//
		foreach ( var tr in TraceBullet( pos, pos + forward * 5000, bulletSize ) )
		{
			tr.Surface.DoBulletImpact( tr );

			if ( !tr.GameObject.IsValid() ) continue;

			if ( tr.GameObject.Components.TryGet<PropHelper>( out var prop ) )
			{
				prop.Damage( damage );
			}
			else if ( tr.GameObject.Components.TryGet<Player>( out var player ) )
			{
				player.TakeDamage( damage );
			}

			// TODO: Make other non-host clients able to apply impulse too
			if ( tr.Body.IsValid() )
			{
				if ( tr.Body.GetComponent() is Rigidbody rigidbody )
				{
					BroadcastApplyImpulseAt( rigidbody, tr.EndPosition, (pos + dir * 5000) * force / tr.Body.Mass );
				}
				else if ( tr.Body.GetComponent() is ModelPhysics modelPhysics )
				{
					BroadcastApplyImpulseAt( modelPhysics, tr.EndPosition, (pos + dir * 5000) * force );
				}
			}
		}
	}

	[Broadcast]
	private void BroadcastApplyImpulseAt( Component body, Vector3 position, Vector3 force )
	{
		if ( !Networking.IsHost ) return;

		if ( body is Rigidbody rigidbody )
		{
			rigidbody.ApplyImpulseAt( position, force );
		}
		else if ( body is ModelPhysics modelPhysics )
		{
			modelPhysics.PhysicsGroup.ApplyImpulse( force / modelPhysics.PhysicsGroup.Mass, true );
		}
	}

	/// <summary>
	/// Shoot a single bullet from owners view point
	/// </summary>
	public virtual void ShootBullet( float spread, float force, float damage, float bulletSize )
	{
		var ray = Owner.AimRay;
		ShootBullet( ray.Position, ray.Forward, spread, force, damage, bulletSize );
	}

	/// <summary>
	/// Shoot a multiple bullets from owners view point
	/// </summary>
	public virtual void ShootBullets( int numBullets, float spread, float force, float damage, float bulletSize )
	{
		var ray = Owner.AimRay;

		for ( int i = 0; i < numBullets; i++ )
		{
			ShootBullet( ray.Position, ray.Forward, spread, force / numBullets, damage, bulletSize );
		}
	}
}