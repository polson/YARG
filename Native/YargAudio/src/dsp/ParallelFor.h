#pragma once

#include <cstddef>
#include <future>
#include <system_error>
#include <vector>

namespace yarg::audio {

template <typename Work>
void forEachParallel(std::size_t count, const Work& work) {
    std::vector<std::future<void>> tasks;
    std::size_t launched = 1;
    for (; launched < count; ++launched) {
        try {
            tasks.push_back(std::async(std::launch::async, work, launched));
        } catch (const std::system_error&) {
            break;
        }
    }
    if (count > 0) work(0);
    for (std::size_t index = launched; index < count; ++index) {
        work(index);
    }
    for (auto& task : tasks) {
        task.get();
    }
}

}
