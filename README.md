# Framework

It is traditional for the creator to magnanimously accept the blame for whatever deficiencies remain. I don’t. Any errors, deficiencies, or problems in this package are somebody else’s fault, but I would appreciate knowing about them so as to determine who is to blame.

# Integration Guide

NOTE: I recommend setting this up before the package manager install as the Character and Enemy scripts will want to serialize with the layers below.

If possible, set up these layers in these spots. The raycasts specifically cycles through these layers and only these layers (fine tuned in layer masks for Characters/Enemies). It can, of course be extended or changed, but as it is this what's supported:

6. Passthrough
7. Character
8. EnemyHazard
9. GeometryAlternate
10. FXPhysics
11. CharacterProjectile
12. CollectableProjectile

"Default" and "Water" are in the collision calculations while skipping the other Unity built ins. Otherwise, all other layers outside of Framework (such as art effects) should be 13 and beyond.

Only 1 tag is used which is WallSlide which is an optional tag (if all walls aren't supported and want to target just specific spots/wall types/etc.) 

Change over to the OLD Input Manager (Project Settings -> Player -> Other Settings : Active Input Handling) I recommend copying over InputManager.asset (this goes in ProjectSettings folder). Message me if I forget to upload to Discord.

Physics 2D Settings (Edit-> Project Settings): 

:ballot_box_with_check: : Queries Start In Colliders

:ballot_box_with_check: : Auto Sync Transforms

CollisionMatrix is as follows:

![image](https://user-images.githubusercontent.com/21694868/169628590-7416c94a-d459-46e5-bdac-971bee0e945f.png)

In Package Manager, don't forget to import the samples to gain access to the Prototype scene examples.

![image](https://user-images.githubusercontent.com/21694868/169628785-630a0898-b7a5-4927-8e5c-4fd7ca3e3f49.png)
