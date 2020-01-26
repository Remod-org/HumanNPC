# HumanNPC
Adds interactive human NPCs which can be modded by other plugins

Adds customizable, human, non-player characters in-game. Make your cities a little bit more lively!

## Features

- Fully configurable
- Can say hi when you get close to them
- Can say goodbye when you get away from them
- Can say something when you try interacting with them (USE)
- Can say ouch when you hit them
- Can say that you are a murderer when you kill them
- Multiple messages are supported (random one chosen)
- Set their name
- Set their kits (Kit plugin required)
- Set Waypoints so they can walk around the map
- Set if they are invulnerable
- Set their respawn time if they die
- NPCs can defend themselves
- Set NPC Chasing speed
- Set NPC Damage
- Set NPC Max Chasing distance
- Set NPC Max View distance
- Set NPC Hostility
- Set NPC Evasion when hit
- Have NPC find and sit in a chair it finds in range
- While chasing or using Waypoints, the NPC will try to detect automatically the best ground position (except during evasion, work pending)

## Teams
- There can exist many teams.
- Every player/NPC can be a part of one or more teams (or none at all).
- Teams can be friends, neutral or foes towards one another and those relationships can be asymmetrical - meaning, team 1 can consider team 2 friends, while team 2 considers team 1 neutral or foes. There exists a fast Dictionary-based relationship cache for looking up whether User 1 considers User 2 a friend, neutral or enemy. This cache gets re-generated every time something about teams/alignments/members changes. It's generated based upon all the teams alignments and possible conflicts. Members of the same team automatically consider each other friends.
- NPCs have some new properties:
  - bool hostiletowardsarmed
  - bool hostiletowardsarmedhard
  - bool raiseAlarm
- hostiletowardsarmed means the NPC will look in the players belt and if it finds any tools, weapons, traps or anything considered "bad" (the list of items is based on categories, but this can be customised/expanded upon) it will attack the player when they cross its interaction radius. If the NPC is supposed to raise the alarm, they will.
- hostiletowardsarmedhard means that when performing the strip-search, the whole inventory is checked. Next step, making this work AFTER the player has crossed the NPC's interaction radius. At the moment, you can approach a strip-searching NPC with no weapon, take it out, and they will do nothing.
- raiseAlarm means the npc will send a distress "signal" (along with a chat message), limited to the number of NPCs that answer it (default 0 = no limit). A visibility check in the sphere of radius based on one of the existing distance values is performed. All the NPCs that the alarm raiser can see will receive this call, but not all of them will answer it. Hostile NPCs will only answer alarm calls from their team members (ignoring teams that are friends with their team). Non-hostile NPCs will answer the calls all friends. I might also implement an extra flag like "white knight" that will make an NPC answer every distress call, what do you think?

## Commands

- /npc_add => create a new NPC and edit it
- /npc_edit [id] => edit the NPC you are looking at or specified ID
- /npc_remove [id] => erase the NPC you are looking at or specified ID
- /npc_end => stop editing an NPC
- /npc OPTION VALUE => set a value of an option of your NPC
- /npc_reset => **removes all NPCs**
- /npc_pathtest => follow NPC path
- /npc_list => list all NPCs
- /npc_way [id] => draws path of the NPC you are looking at or specified ID
- /npc_team => Displays a list of teams, their IDs and their member count
- /npc_team rename [new name] => Gives the team a new name
- /npc_team add [team name, can include spaces] => Adds a team and displays a newly generated ulong ID for it
- /npc_team remove [team name or ID] => Removes a team
- /npc_team empty [team name or ID]
- /npc_team purge => Removes all the team data
- /npc_team member => self-explanatory I hope
- /npc_team member => add
- /npc_team member => remove
- /npc_team member => list
- /npc_team fof [team name or id] => Displays all current 1-sided relationships of the team towards other teams
- /npc_team fof [team name or id] [friend/neutral/foe] [team 2 name or id] => Sets the 1-sided alignment of the team 1 to team 2 (I might implement an optional "mutual" parameter that does the opposite, too - remember, assymetrical relationships)

NPC_ADD  
Creates a new NPC, and edits it.   
It will be created where you stand, and be looking the same way that you are.  Using /npc_add XXXX (npc ID from /npc_list) will clone the NPC to your position

NPC_EDIT  
Edit an NPC (not needed if you just did /npc_add)
Then you can use the command: /npc  
  
NPC_END  
Stop editing an NPC  
  
NPC  
By entering the command alone, you will see what values are currently set.  Option values:  
* attackdistance XX => _Distance between NPC and the target needed for the NPC to ignore the target and go back to spawn_  
* bye reset/"TEXT" "TEXT2" etc => _Dont forgot the \\", this what will be said when the player walks away from the NPC_  
* damageamount XXX => _Damage done by that NPC when he hits a player_
* damagedistance XXX => _Min distance for the NPC to hit a player (3 is default, maybe 20-30 needed for snipers?)_  
* damageinterval XXX =_> Interval in seconds that the NPC has to wait before attacking again_  
* enable true/false =_> Enable (default) or disable the NPC without deleting it (notice that when you are editing a bot it will stay active until you say /npc_end)_  
* radius XXX => _Radius in which the NPC will detect the player_  
* health XXX =>_ To set the Health of the NPC (limited by rust to max 100)_  
* hello reset/"TEXT" "TEXT2" etc => _Dont forgot the \\", this what will be said when the player gets close to the NPC_  
* hurt reset/"TEXT" "TEXT2" etc => _Dont forgot the \\", set a message to tell the player when he hurts the NPC_  
* hostile true/false_ => Set the NPC Hostile, will attack players on sight (radius is the sight limit)_  
* invulnerable true/false => _To set the NPC invulnerable or not_  
* kill reset/"TEXT" "TEXT2" etc => _Dont forgot the \\", set a message to tell the player when he kills the NPC_  
* kit reset/"KitName" => _To set the kit of this NPC, requires the **Kits plugin** (see below)_  
* lootable true/false_ => Set if the NPC is lootable or not_  
* maxdistance XXX => _Max distance from the spawn point that the NPC can run from (while attacking a player)_    
* name "THE NAME" => _To set a name to the NPC_  
* respawn true/false XX =>_ To set it to respawn on death after XX seconds, default is instant respawn_  
* spawn new =>_ To set the new spawn location_  
* speed XXX => _To set the NPC running speed (while chasing a player)_  
* stopandtalk true/false XXX_ => To set if NPC should stop when a player talks to it, and if true for how much time.  
* use reset/"TEXT" "TEXT2" etc => Dont forgot the \\", this what will be said when the player presses USE on the NPC_  
* waypoints reset/"Waypoint list Name" => _To set waypoints of an NPC_  
* hitchance float_ => chance to hit target_  
* fireduration float _=> time to fire_  
* reloadduration float _=> time to reload_  
* defend true/false_ => attack if attacked_  
* evade true/false_ => move if hit while being attacked_
* evdist float_ => how far to move when hit (some randomization is built-in)_
* allowsit_ => Find a chair nearby and sit on spawn_
* follow_ => Follow the attacker as they are running out of range (default is true as with older versions)
* sit_ => Make the NPC sit (toggles allowsit)_
* stand_ => Make the NPC stand (toggles allowsit)_
* needsAmmo true/false_ => needs to have ammo in inventory to shoot_  

NPC WAYPOINTS:  
  You will need to make waypoints with the [Waypoints](https://umod.org/plugins/waypoints#documentation) plugin.  Create a set of waypoints with NAME and use /npc waypoints NAME when editing your NPC.  

NPC KIT:

  You will need the [Kits](https://umod.org/plugins/rust-kits#documentation) plugin.  Create a new kit with the kit plugin like you usually do, then:  
/kit add NAME "random description" -authlevel2 (the level is set so NO players can use the kit, only admins and NPCs)  
Then while editing the NPC do: /npc kit NAME (being the same name as the kit ofc)  
  
NPC ATTACK MOVEMENTS & PATHFINDING:  
  The Pathfinding is still not perfect, but it's getting there.  Currently, the main problem isn't really coming from the Pathfinding but from the HumanNPC plugin because of the way i wrote it, so I'll need to rewrite a part of the plugin to make better movements and player attacks.  
  You will need to download PathFinding for Rust to make the NPC attack movements work.  If the NPC can't find any paths for 5 seconds it will stop targetting the entity and go back to its spawn point with full health.  

## For Developers

Hooks were implemented to allow other plugins to interact with this one.  None of them have return values (can be edited if needed).  New hooks can be added if they make sense.  

**Note:** All NPC have unique userIDs, (BasePlayer.userID), so you may easily save information of NPC by userID.

Called when the NCP is getting hit
```csharp
 OnHitNPC(BasePlayer npc, HitInfo info)
```

Called when the NCP is getting used (pressed use while aiming the NPC)
```csharp
 OnUseNPC(BasePlayer npc, BasePlayer player)
```

Called when a player gets in range of the NPC
```csharp
 OnEnterNPC(BasePlayer npc, BasePlayer player)
```

Called when a player gets out of range of the NPC
```csharp
 OnLeaveNPC(BasePlayer npc, BasePlayer player)
```

Called when an NPC gets killed
```csharp
 OnKillNPC(BasePlayer npc, BasePlayer player)
```

Called when an NPC reachs a waypoint and changes to next waypoint
```csharp
 OnNPCPosition(BasePlayer npc, Vector3 pos)
```

Called when an NPC respawns
```csharp
 OnNPCRespawn(BasePlayer npc)
```

Called when an NPC gets looted
```csharp
 OnLootNPC(PlayerLoot loot, BaseEntity target, string npcId)
```

## Usages

- Make your server more lively with NPCs that talk and interact a bit with players
- Create Epic mobs that spawn every X time and once killed gives loot (use Rust Kits plugin for that)
- Create other plugins that can give quests to players (Hunt RPG is on its way)
- Create other plugins that uses the NPC to manage banks, quests, trades, shops
- Only limited by your imagination - possibilities are infinite!

## Configuration

```json
{
  "Weapon To FX": {
    "pistol_eoka": "fx/weapons/vm_eoka_pistol/attack",
    "pistol_revolver": "fx/weapons/vm_revolver/attack",
    "rifle_ak": "fx/weapons/vm_ak47u/attack",
    "rifle_bolt": "fx/weapons/vm_bolt_rifle/attack",
    "shotgun_pump": "fx/weapons/vm_waterpipe_shotgun/attack",
    "shotgun_waterpipe": "fx/weapons/vm_waterpipe_shotgun/attack",
    "smg_thompson": "fx/weapons/vm_thompson/attack"
  }
}
```

## 3rd party Resources
[HumanNPC Tutorial](https://www.youtube.com/watch?v=okH21H9THaA)

## TODO

- Area call for help
- Friends list not to atack
- Fix evasion
- Follow / attack timeouts

## Not Implementing

- Add bullets animation => not sure I can :/ probably controlled client side
- Different radius for chat & hostile (not going to implement that, too many checks)
- Making this into BotSpawn, et al.
