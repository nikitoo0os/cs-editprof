package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"runtime/debug"
	"strings"
	"time"

	"github.com/nikitoo0os/cs-editprof/tools/demo-parser/internal/parser"
)

type cliError struct {
	exitCode  int
	code      string
	message   string
	retryable bool
}

func (e *cliError) Error() string { return e.message }

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) (exitCode int) {
	var logFile string
	defer func() {
		if recovered := recover(); recovered != nil {
			if logFile != "" {
				_ = os.WriteFile(logFile, debug.Stack(), 0o600)
			}
			writeError(&cliError{99, "UNEXPECTED_ERROR", fmt.Sprint(recovered), false})
			exitCode = 99
		}
	}()

	if len(args) == 0 {
		writeError(&cliError{2, "INVALID_ARGUMENTS", usage(), false})
		return 2
	}
	if args[0] == "version" {
		fmt.Println(parser.ParserVersion)
		return 0
	}

	flags := flag.NewFlagSet(args[0], flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	input := flags.String("input", "", "input .dem path")
	output := flags.String("output", "", "output JSON path")
	pretty := flags.Bool("pretty", false, "indent JSON")
	flags.StringVar(&logFile, "log-file", "", "diagnostic log path")
	includeRaw := flags.Bool("include-raw-events", false, "include raw events when supported")
	if err := flags.Parse(args[1:]); err != nil {
		writeError(&cliError{2, "INVALID_ARGUMENTS", err.Error(), false})
		return 2
	}
	if args[0] != "analyze" && args[0] != "validate" {
		writeError(&cliError{2, "INVALID_ARGUMENTS", usage(), false})
		return 2
	}
	if *input == "" || (args[0] == "analyze" && *output == "") {
		writeError(&cliError{2, "INVALID_ARGUMENTS", "--input and --output are required for analyze; --input is required for validate", false})
		return 2
	}
	if *includeRaw {
		writeLog(logFile, "include-raw-events requested; no raw event payload is emitted by MVP")
	}

	inputPath, err := filepath.Abs(*input)
	if err != nil {
		writeError(&cliError{2, "INVALID_ARGUMENTS", err.Error(), false})
		return 2
	}
	info, err := os.Stat(inputPath)
	if errors.Is(err, os.ErrNotExist) {
		writeError(&cliError{10, "INPUT_NOT_FOUND", "Input demo was not found.", false})
		return 10
	}
	if err != nil {
		writeError(&cliError{12, "INPUT_CANNOT_BE_READ", err.Error(), false})
		return 12
	}
	if info.IsDir() || !strings.EqualFold(filepath.Ext(inputPath), ".dem") {
		writeError(&cliError{11, "INPUT_NOT_DEMO", "Input must be a .dem file.", false})
		return 11
	}

	started := time.Now()
	analysis, err := parser.Analyze(inputPath)
	if err != nil {
		mapped := mapParserError(err)
		writeLog(logFile, fmt.Sprintf("parser failed after %s: %v", time.Since(started), err))
		writeError(mapped)
		return mapped.exitCode
	}
	writeLog(logFile, fmt.Sprintf(
		"parsed map=%s ticks=%d players=%d rounds=%d kills=%d duration=%s",
		analysis.Demo.MapName,
		analysis.Demo.DurationTicks,
		len(analysis.Players),
		len(analysis.Rounds),
		len(analysis.Kills),
		time.Since(started)))
	if args[0] == "validate" {
		fmt.Println(`{"success":true}`)
		return 0
	}

	outputPath, err := filepath.Abs(*output)
	if err != nil {
		writeError(&cliError{30, "OUTPUT_CANNOT_BE_WRITTEN", err.Error(), false})
		return 30
	}
	if err := os.MkdirAll(filepath.Dir(outputPath), 0o755); err != nil {
		writeError(&cliError{30, "OUTPUT_CANNOT_BE_WRITTEN", err.Error(), false})
		return 30
	}
	outputFile, err := os.OpenFile(outputPath, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		writeError(&cliError{30, "OUTPUT_CANNOT_BE_WRITTEN", err.Error(), false})
		return 30
	}
	encoder := json.NewEncoder(outputFile)
	encoder.SetEscapeHTML(false)
	if *pretty {
		encoder.SetIndent("", "  ")
	}
	err = encoder.Encode(analysis)
	closeErr := outputFile.Close()
	if err != nil || closeErr != nil {
		_ = os.Remove(outputPath)
		if err == nil {
			err = closeErr
		}
		writeError(&cliError{30, "OUTPUT_CANNOT_BE_WRITTEN", err.Error(), false})
		return 30
	}
	return 0
}

func mapParserError(err error) *cliError {
	message := err.Error()
	lower := strings.ToLower(message)
	if strings.Contains(lower, "unsupported") {
		return &cliError{21, "UNSUPPORTED_DEMO_VERSION", message, false}
	}
	if strings.Contains(lower, "required") {
		return &cliError{22, "REQUIRED_EVENTS_MISSING", message, false}
	}
	return &cliError{20, "MALFORMED_DEMO", message, false}
}

func writeError(err *cliError) {
	payload := struct {
		Success bool `json:"success"`
		Error   struct {
			Code      string `json:"code"`
			Message   string `json:"message"`
			Retryable bool   `json:"retryable"`
		} `json:"error"`
	}{}
	payload.Error.Code = err.code
	payload.Error.Message = err.message
	payload.Error.Retryable = err.retryable
	_ = json.NewEncoder(os.Stderr).Encode(payload)
}

func writeLog(path, message string) {
	if path == "" {
		return
	}
	_ = os.MkdirAll(filepath.Dir(path), 0o755)
	file, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return
	}
	defer file.Close()
	_, _ = fmt.Fprintf(file, "%s %s\n", time.Now().UTC().Format(time.RFC3339Nano), message)
}

func usage() string {
	return "Usage: demo-parser <analyze|validate|version> [--input path] [--output path]"
}
