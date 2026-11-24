#include "Engine.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <stdexcept>
#include <unordered_map>

namespace glassroom {
namespace {
using Clock = std::chrono::steady_clock;

std::string lower(std::string value)
{
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return value;
}

class DemoEngine final : public Engine {
public:
    explicit DemoEngine(const Json& catalog) : catalog_(catalog)
    {
        for (const auto& album : catalog_.at("albums")) {
            for (const auto& item : album.at("tracks")) {
                Track track{item.at("id"), item.at("title"), item.at("artist"), item.at("albumId"),
                            item.at("album"), item.at("artwork"), item.at("duration"), item.at("number")};
                tracks_.emplace(track.id, track);
                ordered_.push_back(track);
            }
        }
        queue_ = scope(ordered_.front().albumId, {});
    }

    Playback state() override { return {current(), playing_, time(), volume_, queue_, static_cast<int>(index_)}; }

    Json library(const LibraryQuery& request) override
    {
        Json items = Json::array();
        std::size_t total = 0;
        const auto query = lower(request.query);
        const auto append = [&](Json row, std::initializer_list<std::string> values) {
            if (std::none_of(values.begin(), values.end(), [&](const auto& value) {
                return lower(value).find(query) != std::string::npos;
            })) return;
            if (total++ >= request.offset && items.size() < request.limit) items.push_back(std::move(row));
        };

        if (request.kind == "tracks") {
            const auto matches = scope(request.albumId, request.playlistId);
            for (const auto& track : matches) append(track, {track.title, track.artist, track.album});
        } else if (request.kind == "albums") {
            for (auto album : catalog_.at("albums")) {
                album["firstTrackId"] = album.at("tracks").front().at("id");
                album.erase("tracks");
                append(album, {album.at("title"), album.at("artist"), album.at("genre")});
            }
        } else {
            for (auto playlist : catalog_.at("playlists")) {
                playlist.erase("trackIds");
                append(playlist, {playlist.at("title"), playlist.at("description")});
            }
        }
        return {{"items", items}, {"total", total}};
    }

    std::vector<Lyric> lyrics(const std::string& id) override
    {
        std::vector<Lyric> result;
        if (id == "violet-1") for (const auto& line : catalog_.at("lyrics"))
            result.push_back({line.at("at"), line.at("text")});
        return result;
    }

    void play(const PlayRequest& request) override
    {
        if (request.id.empty()) {
            if (!playing_) {
                const auto position = time();
                start(index_, position < current().duration ? position : 0);
            }
            return;
        }
        const auto& track = find(request.id);
        auto queue = scope(request.albumId.empty() ? track.albumId : request.albumId, request.playlistId);
        const auto selected = std::find_if(queue.begin(), queue.end(), [&](const auto& item) { return item.id == track.id; });
        if (selected == queue.end()) throw std::invalid_argument("This track is unavailable.");
        const auto index = static_cast<std::size_t>(selected - queue.begin());
        queue_ = std::move(queue);
        start(index);
    }

    void pause() override { position_ = time(); playing_ = false; }
    void seek(const std::string& id, double seconds) override
    {
        if (current().id != id) throw std::invalid_argument("The active track has changed.");
        position_ = std::clamp(seconds, 0., current().duration);
        started_ = Clock::now();
    }
    void volume(double value) override { volume_ = value; }
    void next() override { if (index_ + 1 < queue_.size()) start(index_ + 1); }
    void previous() override
    {
        if (time() > 3 || index_ == 0) seek(current().id, 0);
        else start(index_ - 1);
    }
    void enqueue(const std::string& id) override { queue_.push_back(find(id)); }
    void remove(std::size_t index, const std::string& id) override
    {
        checkQueue(index, id);
        if (index > index_) queue_.erase(queue_.begin() + static_cast<std::ptrdiff_t>(index));
    }
    void jump(std::size_t index, const std::string& id) override
    {
        checkQueue(index, id);
        start(index);
    }

    Update poll() override
    {
        if (!playing_) return {};
        const auto position = time();
        if (position >= current().duration) {
            if (index_ + 1 < queue_.size()) next();
            else pause();
            return {true, std::nullopt};
        }
        return {false, Progress{current().id, position}};
    }

private:
    const Track& current() const { return queue_[index_]; }
    const Track& find(const std::string& id) const
    {
        const auto track = tracks_.find(id);
        if (track == tracks_.end()) throw std::invalid_argument("This track is unavailable.");
        return track->second;
    }
    void checkQueue(std::size_t index, const std::string& id) const
    {
        if (index >= queue_.size() || queue_[index].id != id) throw std::invalid_argument("The queue has changed.");
    }
    std::vector<Track> scope(const std::string& albumId, const std::string& playlistId) const
    {
        std::vector<Track> result;
        if (!playlistId.empty()) {
            for (const auto& playlist : catalog_.at("playlists")) {
                if (playlist.at("id") != playlistId) continue;
                for (const auto& id : playlist.at("trackIds")) result.push_back(find(id.get<std::string>()));
                break;
            }
        } else {
            std::copy_if(ordered_.begin(), ordered_.end(), std::back_inserter(result),
                         [&](const auto& track) { return albumId.empty() || track.albumId == albumId; });
        }
        return result;
    }
    double time() const
    {
        const auto elapsed = playing_ ? std::chrono::duration<double>(Clock::now() - started_).count() : 0.;
        return std::min(position_ + elapsed, current().duration);
    }
    void start(std::size_t index, double position = 0)
    {
        index_ = index;
        position_ = position;
        started_ = Clock::now();
        playing_ = true;
    }

    Json catalog_;
    std::unordered_map<std::string, Track> tracks_;
    std::vector<Track> ordered_;
    std::vector<Track> queue_;
    std::size_t index_{};
    bool playing_{};
    double position_{};
    double volume_{0.7};
    Clock::time_point started_{Clock::now()};
};
}

std::unique_ptr<Engine> makeDemoEngine(const Json& catalog) { return std::make_unique<DemoEngine>(catalog); }
}
