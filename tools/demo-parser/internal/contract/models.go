package contract

type Analysis struct {
	SchemaVersion string       `json:"schemaVersion"`
	Parser        ParserInfo   `json:"parser"`
	Demo          DemoMetadata `json:"demo"`
	Players       []Player     `json:"players"`
	Rounds        []Round      `json:"rounds"`
	Kills         []Kill       `json:"kills"`
	Warnings      []string     `json:"warnings"`
}

type ParserInfo struct {
	Name    string `json:"name"`
	Version string `json:"version"`
}

type DemoMetadata struct {
	FileName             string `json:"fileName"`
	MapName              string `json:"mapName"`
	TickRate             int    `json:"tickRate"`
	DurationTicks        int64  `json:"durationTicks"`
	DurationMilliseconds *int64 `json:"durationMilliseconds"`
}

type Player struct {
	PlayerID string  `json:"playerId"`
	SteamID  *string `json:"steamId"`
	Name     string  `json:"name"`
}

type Round struct {
	RoundNumber   int     `json:"roundNumber"`
	StartTick     int64   `json:"startTick"`
	FreezeEndTick *int64  `json:"freezeEndTick"`
	EndTick       int64   `json:"endTick"`
	Winner        *string `json:"winner"`
	Reason        *string `json:"reason"`
}

type Kill struct {
	EventIndex         int      `json:"eventIndex"`
	Tick               int64    `json:"tick"`
	RoundNumber        int      `json:"roundNumber"`
	KillerPlayerID     *string  `json:"killerPlayerId"`
	KillerName         *string  `json:"killerName"`
	VictimPlayerID     string   `json:"victimPlayerId"`
	VictimName         string   `json:"victimName"`
	AssisterPlayerID   *string  `json:"assisterPlayerId"`
	Weapon             string   `json:"weapon"`
	Headshot           bool     `json:"headshot"`
	KillerTeam         *string  `json:"killerTeam"`
	VictimTeam         *string  `json:"victimTeam"`
	Wallbang           *bool    `json:"wallbang"`
	OneTap             *bool    `json:"oneTap"`
	NoScope            *bool    `json:"noScope"`
	ThroughSmoke       *bool    `json:"throughSmoke"`
	RoundEndingKill    *bool    `json:"roundEndingKill"`
	LastEnemyKill      *bool    `json:"lastEnemyKill"`
	KillerHealth       *int     `json:"killerHealth"`
	DistanceMeters     *float64 `json:"distanceMeters"`
	ShotsSinceLastKill *int     `json:"shotsSinceLastKill"`
}
