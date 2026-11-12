---
name: unity-rigging-and-animation-expert
description: Molecular-level character rigging, Two-Bone IK setup, HumanPose muscle posing, and AnimationClip (.anim) generation for Unity 6.
---

# Unity 6 Character Rigging, Posing & Animation Skill

Whenever asked to rig, pose, or animate a 3D character or weapon in Unity 6:
1. **NEVER Guess Raw Bone Euler Angles:** Every 3D rig has different local bone roll axes. Guessing Euler angles twists limbs unnaturally.
2. **Use `Scorched.EditorTools.AIRiggingAndAnimationToolkit.ApplyHumanoidMusclePose` for Posing:**
   - Pose Humanoid characters using standardized `HumanTrait.MuscleName` values (`-1.0f` to `+1.0f`).
   - Finger curls around weapon grips use `"Left/Right Thumb/Index/Middle/Ring/Little 1..3 Stretched"` set to `-0.75f`.
3. **Use `Scorched.EditorTools.AIRiggingAndAnimationToolkit.SolveTwoBoneIK` or `TwoBoneIKConstraint` for Weapon Grips & Reloads:**
   - Always inspect `Renderer.bounds` of the weapon/magazine first.
   - Create empty child Transforms on the weapon (`RightHandGripTarget`, `LeftHandGripTarget`, `MagGrabTarget`).
   - Solve `UpperArm -> ForeArm -> Hand` mathematically toward the target Transform so hands lock onto the exact 3D coordinates of the gun or magazine.
4. **Bake `.anim` Clips via `RecordBoneKeyframe` & `BakeCurvesToClip`:**
   - Step through time `t`, solve IK + Muscle Poses at each step, call `RecordBoneKeyframe` on the affected bones, and call `BakeCurvesToClip("Assets/Animations/MyClip.anim", curves)` which automatically runs `clip.EnsureQuaternionContinuity()`.
5. **Direct Script Animation Playback Compatibility:**
   - In this project, animations are triggered directly by state name in code (`animator.CrossFadeInFixedTime("StateName", 0.15f)`) rather than Animator Parameters. When adding new `.anim` clips to an `AnimatorController`, add the state directly to the Base Layer with the exact requested state name.

