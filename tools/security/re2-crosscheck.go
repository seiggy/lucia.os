// Replays the corpus Lucia's DomainChecks write (LUCIA_RE2_CORPUS) with Go's regexp, which is RE2 as CrowdSec runs it.
// Every pattern Lucia accepts must compile here and match exactly the samples .NET matched, so the breadth checks Lucia runs
// in .NET hold for the engine too. Patterns Lucia refuses are reported with what RE2 would have matched.
//
//	LUCIA_RE2_CORPUS=$PWD/re2-corpus.json dotnet run --project tests/Lucia.Homelab.DomainChecks
//	docker run --rm -v "$PWD:/w" -w /w golang:1.25-alpine go run tools/security/re2-crosscheck.go re2-corpus.json
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"regexp"
)

type corpus struct {
	Samples  []string `json:"samples"`
	Patterns []struct {
		Pattern  string  `json:"pattern"`
		Accepted bool    `json:"accepted"`
		Matches  []*bool `json:"matches"`
	} `json:"patterns"`
}

func main() {
	if len(os.Args) != 2 {
		fmt.Fprintln(os.Stderr, "usage: re2-crosscheck <corpus.json>")
		os.Exit(2)
	}
	data, err := os.ReadFile(os.Args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	var c corpus
	if err := json.Unmarshal(data, &c); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(2)
	}
	failed, accepted := 0, 0
	for _, p := range c.Patterns {
		re, err := regexp.Compile(p.Pattern)
		if !p.Accepted {
			if err != nil {
				fmt.Printf("refused %-28q RE2 can't compile it either\n", p.Pattern)
				continue
			}
			n := 0
			for _, s := range c.Samples {
				if re.MatchString(s) {
					n++
				}
			}
			fmt.Printf("refused %-28q RE2 would match %d of %d samples\n", p.Pattern, n, len(c.Samples))
			continue
		}
		accepted++
		if err != nil {
			fmt.Printf("FAIL    %q: Lucia accepts it, RE2 doesn't compile it: %v\n", p.Pattern, err)
			failed++
			continue
		}
		for i, s := range c.Samples {
			if p.Matches[i] == nil || re.MatchString(s) != *p.Matches[i] {
				fmt.Printf("FAIL    %q on %q: .NET %v, RE2 %v\n", p.Pattern, s, p.Matches[i] != nil && *p.Matches[i], re.MatchString(s))
				failed++
			}
		}
	}
	if failed > 0 {
		fmt.Printf("%d disagreements\n", failed)
		os.Exit(1)
	}
	fmt.Printf("PASS: %d accepted patterns match the same %d samples in RE2 as in .NET\n", accepted, len(c.Samples))
}
