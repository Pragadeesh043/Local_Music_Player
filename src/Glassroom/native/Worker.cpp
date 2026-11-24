#include "Worker.hpp"

#include <chrono>
#include <exception>
#include <utility>

namespace glassroom {
using namespace std::chrono_literals;

Worker::Worker(std::unique_ptr<Engine> engine, Sink sink)
    : engine_(std::move(engine)), sink_(std::move(sink)), thread_([this](std::stop_token stop) { run(stop); }) {}

Worker::~Worker()
{
    thread_.request_stop();
    wake_.notify_all();
}

bool Worker::submit(int id, std::string method, Json args)
{
    std::lock_guard lock(mutex_);
    if (jobs_.size() >= 128) return false;
    jobs_.push_back({id, std::move(method), std::move(args)});
    wake_.notify_one();
    return true;
}

void Worker::run(std::stop_token stop)
{
    using Clock = std::chrono::steady_clock;
    auto tick = Clock::now();
    while (!stop.stop_requested()) {
        std::optional<Job> job;
        {
            std::unique_lock lock(mutex_);
            wake_.wait_until(lock, stop, tick, [this] { return !jobs_.empty(); });
            if (stop.stop_requested()) break;
            if (!jobs_.empty()) {
                job = std::move(jobs_.front());
                jobs_.pop_front();
            }
        }

        if (job) {
            try {
                auto reply = dispatch(*engine_, job->method, job->args);
                if (reply.stateChanged) sink_(event("state", engine_->state()), false);
                sink_(success(job->id, std::move(reply.result)), false);
            } catch (const std::invalid_argument& error) {
                sink_(failure(job->id, error.what()), false);
            } catch (const Json::exception&) {
                sink_(failure(job->id, "Invalid bridge data."), false);
            } catch (const std::exception&) {
                sink_(failure(job->id, "The audio engine could not complete this action."), false);
            }
        }

        if (Clock::now() < tick) continue;
        tick = Clock::now() + 125ms;
        try {
            const auto update = engine_->poll();
            if (update.stateChanged) sink_(event("state", engine_->state()), false);
            else if (update.progress) sink_(event("progress", *update.progress), true);
        } catch (const std::exception&) {
            // Adapters should surface persistent backend errors through their state or commands.
        }
    }
}
}
