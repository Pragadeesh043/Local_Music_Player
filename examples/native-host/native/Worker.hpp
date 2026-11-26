#pragma once

#include "Protocol.hpp"

#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>

namespace glassroom {
class Worker {
public:
    using Sink = std::function<void(Json, bool replaceable)>;

    Worker(std::unique_ptr<Engine> engine, Sink sink);
    ~Worker();
    bool submit(int id, std::string method, Json args);

private:
    struct Job { int id; std::string method; Json args; };
    void run(std::stop_token stop);

    std::unique_ptr<Engine> engine_;
    Sink sink_;
    std::mutex mutex_;
    std::condition_variable_any wake_;
    std::deque<Job> jobs_;
    std::jthread thread_;
};
}
