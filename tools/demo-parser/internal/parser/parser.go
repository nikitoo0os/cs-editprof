package parser

import (
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"unicode/utf8"

	demoinfocs "github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/common"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/events"
	"github.com/markus-wa/demoinfocs-golang/v5/pkg/demoinfocs/msg"

	"github.com/nikitoo0os/cs-editprof/tools/demo-parser/internal/contract"
)

const (
	SchemaVersion = "1.0"
	ParserName    = "cs2-demo-parser"
	ParserVersion = "0.1.0"
)

type roundBuilder struct {
	number        int
	startTick     int64
	freezeEndTick *int64
}

func Analyze(path string) (contract.Analysis, error) {
	file, err := os.Open(path)
	if err != nil {
		return contract.Analysis{}, fmt.Errorf("open demo: %w", err)
	}
	defer file.Close()

	p := demoinfocs.NewParser(file)
	defer p.Close()

	result := contract.Analysis{
		SchemaVersion: SchemaVersion,
		Parser:        contract.ParserInfo{Name: ParserName, Version: ParserVersion},
		Demo:          contract.DemoMetadata{FileName: filepath.Base(path)},
		Players:       []contract.Player{},
		Rounds:        []contract.Round{},
		Kills:         []contract.Kill{},
		Warnings:      []string{},
	}
	players := make(map[string]contract.Player)
	matchStarted := false
	var currentRound *roundBuilder
	roundNumber := 0

	currentTick := func() int64 {
		return int64(p.GameState().IngameTick())
	}
	collectPlayer := func(player *common.Player) {
		if player == nil {
			return
		}
		mapped := mapPlayer(player)
		players[mapped.PlayerID] = mapped
	}
	collectParticipants := func() {
		for _, player := range p.GameState().Participants().All() {
			collectPlayer(player)
		}
	}
	startMatch := func() {
		if matchStarted {
			return
		}
		matchStarted = true
		roundNumber = 0
		currentRound = nil
		result.Rounds = result.Rounds[:0]
		result.Kills = result.Kills[:0]
		collectParticipants()
	}

	p.RegisterNetMessageHandler(func(serverInfo *msg.CSVCMsg_ServerInfo) {
		if mapName := serverInfo.GetMapName(); mapName != "" {
			result.Demo.MapName = mapName
		}
	})
	p.RegisterEventHandler(func(events.MatchStart) { startMatch() })
	p.RegisterEventHandler(func(event events.MatchStartedChanged) {
		if event.NewIsStarted {
			startMatch()
		}
	})
	p.RegisterEventHandler(func(events.AnnouncementMatchStarted) { startMatch() })
	p.RegisterEventHandler(func(events.RoundStart) {
		if !matchStarted && p.GameState().IsMatchStarted() {
			startMatch()
		}
		if !matchStarted {
			return
		}
		roundNumber++
		currentRound = &roundBuilder{number: roundNumber, startTick: currentTick()}
		collectParticipants()
	})
	p.RegisterEventHandler(func(events.RoundFreezetimeEnd) {
		if currentRound == nil {
			return
		}
		tick := currentTick()
		currentRound.freezeEndTick = &tick
	})
	p.RegisterEventHandler(func(event events.RoundEnd) {
		if currentRound == nil {
			return
		}
		result.Rounds = append(result.Rounds, contract.Round{
			RoundNumber:   currentRound.number,
			StartTick:     currentRound.startTick,
			FreezeEndTick: currentRound.freezeEndTick,
			EndTick:       currentTick(),
			Winner:        teamName(event.Winner),
			Reason:        roundEndReason(event.Reason),
		})
		currentRound = nil
		collectParticipants()
	})
	p.RegisterEventHandler(func(event events.Kill) {
		if !matchStarted || currentRound == nil || event.Victim == nil {
			return
		}
		collectPlayer(event.Killer)
		collectPlayer(event.Victim)
		collectPlayer(event.Assister)
		killerID, killerName, killerTeam := nullablePlayer(event.Killer)
		assisterID, _, _ := nullablePlayer(event.Assister)
		victim := mapPlayer(event.Victim)
		weapon := "unknown"
		if event.Weapon != nil {
			weapon = event.Weapon.String()
		}
		result.Kills = append(result.Kills, contract.Kill{
			EventIndex:       len(result.Kills) + 1,
			Tick:             currentTick(),
			RoundNumber:      currentRound.number,
			KillerPlayerID:   killerID,
			KillerName:       killerName,
			VictimPlayerID:   victim.PlayerID,
			VictimName:       victim.Name,
			AssisterPlayerID: assisterID,
			Weapon:           weapon,
			Headshot:         event.IsHeadshot,
			KillerTeam:       killerTeam,
			VictimTeam:       teamName(event.Victim.Team),
		})
	})

	if err := p.ParseToEnd(); err != nil {
		return contract.Analysis{}, fmt.Errorf("parse demo: %w", err)
	}
	collectParticipants()

	result.Demo.TickRate = int(math.Round(p.TickRate()))
	result.Demo.DurationTicks = int64(p.GameState().IngameTick())
	if result.Demo.DurationTicks <= 0 {
		result.Demo.DurationTicks = int64(p.CurrentFrame())
		result.Warnings = append(result.Warnings, "Server in-game tick was unavailable; duration uses demo frames.")
	}
	if elapsed := p.CurrentTime(); elapsed > 0 {
		ms := elapsed.Milliseconds()
		result.Demo.DurationMilliseconds = &ms
	}
	if result.Demo.MapName == "" {
		result.Warnings = append(result.Warnings, "Map name was not emitted by the demo.")
	}
	if currentRound != nil {
		result.Warnings = append(result.Warnings, "The final round did not emit RoundEnd and was omitted.")
	}

	for _, player := range players {
		result.Players = append(result.Players, player)
	}
	sort.Slice(result.Players, func(i, j int) bool {
		return result.Players[i].PlayerID < result.Players[j].PlayerID
	})
	sort.SliceStable(result.Kills, func(i, j int) bool {
		if result.Kills[i].Tick == result.Kills[j].Tick {
			return result.Kills[i].EventIndex < result.Kills[j].EventIndex
		}
		return result.Kills[i].Tick < result.Kills[j].Tick
	})
	for index := range result.Kills {
		result.Kills[index].EventIndex = index + 1
	}

	if result.Demo.TickRate <= 0 {
		return contract.Analysis{}, fmt.Errorf("required tick rate could not be extracted")
	}
	if len(result.Rounds) == 0 {
		return contract.Analysis{}, fmt.Errorf("required round events could not be extracted")
	}
	return result, nil
}

func mapPlayer(player *common.Player) contract.Player {
	name := truncateUTF8(player.Name, 128)
	if player.SteamID64 == 0 {
		return contract.Player{
			PlayerID: "user:" + strconv.Itoa(player.UserID),
			SteamID:  nil,
			Name:     name,
		}
	}
	steamID := strconv.FormatUint(player.SteamID64, 10)
	return contract.Player{PlayerID: steamID, SteamID: &steamID, Name: name}
}

func nullablePlayer(player *common.Player) (*string, *string, *string) {
	if player == nil {
		return nil, nil, nil
	}
	mapped := mapPlayer(player)
	id := mapped.PlayerID
	name := mapped.Name
	return &id, &name, teamName(player.Team)
}

func teamName(team common.Team) *string {
	var name string
	switch team {
	case common.TeamTerrorists:
		name = "T"
	case common.TeamCounterTerrorists:
		name = "CT"
	default:
		return nil
	}
	return &name
}

func roundEndReason(reason events.RoundEndReason) *string {
	names := map[events.RoundEndReason]string{
		events.RoundEndReasonTargetBombed:        "TargetBombed",
		events.RoundEndReasonBombDefused:         "BombDefused",
		events.RoundEndReasonCTWin:               "CTWin",
		events.RoundEndReasonTerroristsWin:       "TerroristsWin",
		events.RoundEndReasonDraw:                "Draw",
		events.RoundEndReasonTargetSaved:         "TargetSaved",
		events.RoundEndReasonTerroristsSurrender: "TerroristsSurrender",
		events.RoundEndReasonCTSurrender:         "CTSurrender",
	}
	name, ok := names[reason]
	if !ok {
		return nil
	}
	return &name
}

func truncateUTF8(value string, maxRunes int) string {
	if utf8.RuneCountInString(value) <= maxRunes {
		return value
	}
	return string([]rune(value)[:maxRunes])
}
