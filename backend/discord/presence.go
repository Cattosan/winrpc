package discord

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/hugolgst/rich-go/client"
)

var (
	connected = false
	lastClientID string
)

func Init(clientID string) error {
	lastClientID = clientID
	if connected {
		client.Logout()
	}
	err := client.Login(clientID)
	if err == nil {
		connected = true
	}
	return err
}

func Disconnect() {
	if connected {
		client.Logout()
		connected = false
	}
}

type CoverResult struct {
	Url           string
	MatchedArtist string
	MatchedAlbum  string
	Source        string
}

type iTunesResult struct {
	Results []struct {
		ArtworkUrl100  string `json:"artworkUrl100"`
		CollectionName string `json:"collectionName"`
		TrackName      string `json:"trackName"`
		ArtistName     string `json:"artistName"`
	} `json:"results"`
}

var (
	jpPattern      = regexp.MustCompile(`[\p{Hiragana}\p{Katakana}]`)
	cjkPattern     = regexp.MustCompile(`[\p{Han}]`)
	ruPattern      = regexp.MustCompile(`[\p{Cyrillic}]`)
	bracketCleaner = regexp.MustCompile(`[『』「」【】]`)
)

func detectCountry(s string) string {
	if jpPattern.MatchString(s) {
		return "JP"
	}
	if cjkPattern.MatchString(s) {
		return "CN"
	}
	if ruPattern.MatchString(s) {
		return "RU"
	}
	return "US"
}

func cleanTerm(s string) string {
	return strings.TrimSpace(bracketCleaner.ReplaceAllString(s, " "))
}

func primaryArtist(artist string) string {
	for _, sep := range []string{" / ", "/", " feat. ", " ft. ", " & "} {
		parts := strings.SplitN(artist, sep, 2)
		if len(parts) > 1 && strings.TrimSpace(parts[0]) != "" {
			return strings.TrimSpace(parts[0])
		}
	}
	return artist
}

type mbSearchResult struct {
	Releases []struct {
		ID    string `json:"id"`
		Title string `json:"title"`
		ArtistCredit []struct {
			Name string `json:"name"`
		} `json:"artist-credit"`
	} `json:"releases"`
}

type lastFmResult struct {
	Results struct {
		AlbumMatches struct {
			Album []struct {
				Name   string `json:"name"`
				Artist string `json:"artist"`
				Image  []struct {
					Text string `json:"#text"`
					Size string `json:"size"`
				} `json:"image"`
			} `json:"album"`
		} `json:"albummatches"`
	} `json:"results"`
}

var lastFmApiKey string

func SetLastFmKey(key string) {
	lastFmApiKey = strings.TrimSpace(key)
}

func normalizeForMatch(s string) string {
	s = strings.ToLower(s)
	// We remove spaces, dots (all types), brackets, and dashes to make matching extremely robust.
	replacer := strings.NewReplacer(
		" ", "", ".", "", "．", "", "『", "", "』", "", "「", "", "」", "", "【", "", "】", "", "-", "", "・", "", "~", "", "～", "",
	)
	return replacer.Replace(s)
}

func scoreCover(targetArtist, targetAlbum string, result *CoverResult) int {
	if result == nil || result.Url == "" {
		return -1
	}

	score := 0
	normTargetArtist := normalizeForMatch(targetArtist)
	normTargetAlbum := normalizeForMatch(targetAlbum)
	normResArtist := normalizeForMatch(result.MatchedArtist)
	normResAlbum := normalizeForMatch(result.MatchedAlbum)

	if normTargetAlbum != "" {
		if normTargetAlbum == normResAlbum {
			score += 100
		} else if strings.Contains(normResAlbum, normTargetAlbum) || strings.Contains(normTargetAlbum, normResAlbum) {
			score += 50
		}
	}

	if normTargetArtist != "" {
		if normTargetArtist == normResArtist {
			score += 100
		} else if strings.Contains(normResArtist, normTargetArtist) || strings.Contains(normTargetArtist, normResArtist) {
			score += 50
		}
	}

	return score
}

func fetchCoverFromLastFm(artist, album string) *CoverResult {
	if lastFmApiKey == "" {
		return nil
	}

	cleanAlbum := cleanTerm(album)

	searchTerm := cleanAlbum
	apiUrl := fmt.Sprintf("https://ws.audioscrobbler.com/2.0/?method=album.search&album=%s&api_key=%s&format=json&limit=30",
		url.QueryEscape(searchTerm), lastFmApiKey)

	c := http.Client{Timeout: 3 * time.Second}
	resp, err := c.Get(apiUrl)
	if err != nil {
		return nil
	}
	defer resp.Body.Close()

	var result lastFmResult
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return nil
	}

	if len(result.Results.AlbumMatches.Album) == 0 {
		return nil
	}

	albums := result.Results.AlbumMatches.Album
	normTarget := normalizeForMatch(album)
	
	// First pass: try to find an exact normalized match
	var bestAlbumIndex int = -1
	for i, a := range albums {
		normResult := normalizeForMatch(a.Name)
		if normResult == normTarget {
			bestAlbumIndex = i
			break
		}
	}

	// Second pass: try to find a partial match
	if bestAlbumIndex == -1 {
		for i, a := range albums {
			normResult := normalizeForMatch(a.Name)
			if strings.Contains(normResult, normTarget) || strings.Contains(normTarget, normResult) {
				bestAlbumIndex = i
				break
			}
		}
	}

	// Fallback to the first result if no matches found
	if bestAlbumIndex == -1 {
		bestAlbumIndex = 0
	}

	bestAlbum := albums[bestAlbumIndex]
	for i := len(bestAlbum.Image) - 1; i >= 0; i-- {
		img := bestAlbum.Image[i]
		if img.Text != "" && !strings.Contains(img.Text, "2a96cbd8b46e442fc41c2b86b821562f") {
			return &CoverResult{
				Url:           img.Text,
				MatchedArtist: bestAlbum.Artist,
				MatchedAlbum:  bestAlbum.Name,
				Source:        "lastfm",
			}
		}
	}

	return nil
}

func fetchCoverFromMusicBrainz(artist, album string) *CoverResult {
	mainArtist := primaryArtist(artist)
	cleanAlbum := cleanTerm(album)

	queries := []string{
		fmt.Sprintf(`release:"%s" AND artist:"%s"`, cleanAlbum, mainArtist),
		fmt.Sprintf(`release:"%s"`, cleanAlbum),
	}

	c := http.Client{
		Timeout: 3 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}

	for _, query := range queries {
		searchUrl := fmt.Sprintf("https://musicbrainz.org/ws/2/release/?query=%s&fmt=json&limit=1", url.QueryEscape(query))

		req, err := http.NewRequest("GET", searchUrl, nil)
		if err != nil {
			continue
		}
		req.Header.Set("User-Agent", "WinRPC/1.0 (https://github.com/winrpc)")

		resp, err := c.Do(req)
		if err != nil {
			continue
		}

		var result mbSearchResult
		err = json.NewDecoder(resp.Body).Decode(&result)
		resp.Body.Close()

		if err != nil || len(result.Releases) == 0 {
			continue
		}

		mbid := result.Releases[0].ID
		coverUrl := fmt.Sprintf("https://coverartarchive.org/release/%s/front-500", mbid)

		headResp, err := c.Head(coverUrl)
		if err != nil {
			continue
		}
		headResp.Body.Close()

		if headResp.StatusCode == 200 || headResp.StatusCode == 307 {
			urlStr := coverUrl
			if loc := headResp.Header.Get("Location"); loc != "" {
				urlStr = loc
			}
			
			matchedArtist := ""
			if len(result.Releases[0].ArtistCredit) > 0 {
				matchedArtist = result.Releases[0].ArtistCredit[0].Name
			}

			return &CoverResult{
				Url:           urlStr,
				MatchedArtist: matchedArtist,
				MatchedAlbum:  result.Releases[0].Title,
				Source:        "musicbrainz",
			}
		}
	}

	return nil
}

func fetchCoverFromItunes(title, artist, album, country string) *CoverResult {
	cleanAlbum := cleanTerm(album)
	cleanTitle := cleanTerm(title)
	mainArtist := primaryArtist(artist)

	queries := []struct {
		term   string
		entity string
	}{
		{mainArtist + " " + cleanAlbum, "album"},
		{cleanAlbum, "album"},
		{mainArtist + " " + cleanTitle, "song"},
		{cleanTitle, "song"},
		{mainArtist, "musicArtist"},
	}

	c := http.Client{Timeout: 3 * time.Second}
	normTargetAlbum := normalizeForMatch(album)
	normTargetTitle := normalizeForMatch(title)

	for _, q := range queries {
		term := strings.TrimSpace(q.term)
		if term == "" || term == "Unknown Album" || term == "Unknown Artist" || term == "Unknown Track" {
			continue
		}

		queryUrl := fmt.Sprintf("https://itunes.apple.com/search?term=%s&entity=%s&country=%s&limit=30", url.QueryEscape(term), q.entity, country)
		resp, err := c.Get(queryUrl)
		if err != nil {
			continue
		}

		var result iTunesResult
		err = json.NewDecoder(resp.Body).Decode(&result)
		resp.Body.Close()

		if err != nil || len(result.Results) == 0 {
			continue
		}

		bestIndex := -1
		// First pass: try exact match
		for i, res := range result.Results {
			if res.ArtworkUrl100 == "" {
				continue
			}
			normColl := normalizeForMatch(res.CollectionName)
			normTrack := normalizeForMatch(res.TrackName)
			if (normTargetAlbum != "" && normColl == normTargetAlbum) || 
			   (normTargetTitle != "" && normTrack == normTargetTitle) {
				bestIndex = i
				break
			}
		}

		// Second pass: try partial match
		if bestIndex == -1 {
			for i, res := range result.Results {
				if res.ArtworkUrl100 == "" {
					continue
				}
				normColl := normalizeForMatch(res.CollectionName)
				normTrack := normalizeForMatch(res.TrackName)
				if (normTargetAlbum != "" && (strings.Contains(normColl, normTargetAlbum) || strings.Contains(normTargetAlbum, normColl))) ||
				   (normTargetTitle != "" && (strings.Contains(normTrack, normTargetTitle) || strings.Contains(normTargetTitle, normTrack))) {
					bestIndex = i
					break
				}
			}
		}

		if bestIndex == -1 {
			// Find first valid one
			for i, res := range result.Results {
				if res.ArtworkUrl100 != "" {
					bestIndex = i
					break
				}
			}
		}

		if bestIndex != -1 && result.Results[bestIndex].ArtworkUrl100 != "" {
			res := result.Results[bestIndex]
			return &CoverResult{
				Url:           strings.Replace(res.ArtworkUrl100, "100x100bb", "512x512bb", 1),
				MatchedArtist: res.ArtistName,
				MatchedAlbum:  res.CollectionName,
				Source:        "itunes",
			}
		}
	}
	return nil
}

func FetchCoverArt(title, artist, album string) string {
	combined := title + artist + album
	country := detectCountry(combined)

	var wg sync.WaitGroup
	var lastFmRes, mbRes, itunesRes *CoverResult

	wg.Add(3)
	go func() {
		defer wg.Done()
		lastFmRes = fetchCoverFromLastFm(artist, album)
	}()
	go func() {
		defer wg.Done()
		mbRes = fetchCoverFromMusicBrainz(artist, album)
	}()
	go func() {
		defer wg.Done()
		itunesRes = fetchCoverFromItunes(title, artist, album, country)
	}()

	wg.Wait()

	var candidates []*CoverResult
	if lastFmRes != nil {
		candidates = append(candidates, lastFmRes)
	}
	if mbRes != nil {
		candidates = append(candidates, mbRes)
	}
	if itunesRes != nil {
		candidates = append(candidates, itunesRes)
	}

	if len(candidates) == 0 {
		return "music_icon"
	}

	var bestMatch *CoverResult
	var highestScore = -1

	for _, cand := range candidates {
		score := scoreCover(artist, album, cand)
		
		// Apply tie-breaker rules
		if score > highestScore {
			highestScore = score
			bestMatch = cand
		} else if score == highestScore && score != -1 {
			// Tie-breaker based on regional preference
			if country == "JP" || country == "CN" || country == "RU" {
				// Prefer Last.fm > MusicBrainz > iTunes
				if cand.Source == "lastfm" || (cand.Source == "musicbrainz" && bestMatch.Source == "itunes") {
					bestMatch = cand
				}
			} else {
				// Prefer iTunes > Last.fm > MusicBrainz
				if cand.Source == "itunes" || (cand.Source == "lastfm" && bestMatch.Source == "musicbrainz") {
					bestMatch = cand
				}
			}
		}
	}

	if bestMatch != nil && bestMatch.Url != "" {
		return bestMatch.Url
	}

	return "music_icon"
}

func UpdatePresence(title, artist, album string, quality string, start time.Time, end time.Time) (string, error) {
	if !connected {
		if lastClientID != "" {
			err := client.Login(lastClientID)
			if err == nil {
				connected = true
			} else {
				return "", err
			}
		} else {
			return "", fmt.Errorf("discord not connected")
		}
	}

	coverUrl := FetchCoverArt(title, artist, album)

	hoverText := album
	if quality != "" && quality != "Standard Audio" {
		hoverText = fmt.Sprintf("%s [%s]", album, quality)
	}

	return coverUrl, client.SetActivity(client.Activity{
		Details:    title,
		State:      artist,
		LargeImage: coverUrl,
		LargeText:  hoverText,
		Type:       2, // 2 = Listening
		Timestamps: &client.Timestamps{
			Start: &start,
			End:   &end,
		},
	})
}

func ClearPresence() {
	if connected {
		client.Logout()
		connected = false
	}
}
